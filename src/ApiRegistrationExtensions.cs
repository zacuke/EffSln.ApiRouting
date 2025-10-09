using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace EffSln.ApiRouting;

/// <summary>
/// Provides extension methods for registering and mapping API endpoints.
/// </summary>
public static class ApiRegistrationExtensions
{
    /// <summary>
    /// Registers API endpoint types from the calling assembly in the service collection.
    /// </summary>
    /// <param name="services">The service collection to register endpoints in.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddApiEndpoints(this IServiceCollection services)
    {
        var endpointTypes = Assembly.GetCallingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract &&
                (t.GetCustomAttributes<HttpMethodAttribute>().Any() ||
                 t.GetMethods().Any(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())));

        foreach (var type in endpointTypes)
        {
            services.AddTransient(type);
        }

        return services;
    }

    /// <summary>
    /// Maps API endpoints from the calling assembly to a route group.
    /// </summary>
    /// <param name="group">The route group builder to map endpoints to.</param>
    /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
    /// <returns>The endpoint convention builder for chaining.</returns>
    public static IEndpointConventionBuilder MapApiEndpoints(this RouteGroupBuilder group, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiRegistrationExtensions));

        var endpointTypes = Assembly.GetCallingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract &&
                (t.GetCustomAttributes<HttpMethodAttribute>().Any() ||
                 t.GetMethods().Any(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())));

        var endpointBuilders = new List<IEndpointConventionBuilder>();

        foreach (var type in endpointTypes)
        {
            var (httpMethod, handlerMethod) = GetHttpMethodAndHandlerFromType(type);

            if (httpMethod != null && handlerMethod != null)
            {
                var route = GetRouteFromType(type);
                logger.LogInformation("Registered API: {HttpMethod} {Route}", httpMethod.ToUpper(), route);
                var endpointBuilder = RegisterEndpoint(group, type, httpMethod, handlerMethod, serviceProvider);
                endpointBuilders.Add(endpointBuilder);
            }
        }

        return new CompositeEndpointConventionBuilder(endpointBuilders);
    }

    /// <summary>
    /// Maps API endpoints from the calling assembly to a web application.
    /// </summary>
    /// <param name="app">The web application to map endpoints to.</param>
    /// <returns>The endpoint convention builder for chaining.</returns>
    public static IEndpointConventionBuilder MapApiEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiRegistrationExtensions));

        var endpointTypes = Assembly.GetCallingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract &&
                (t.GetCustomAttributes<HttpMethodAttribute>().Any() ||
                 t.GetMethods().Any(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())));

        var endpointBuilders = new List<IEndpointConventionBuilder>();

        foreach (var type in endpointTypes)
        {
            var (httpMethod, handlerMethod) = GetHttpMethodAndHandlerFromType(type);

            if (httpMethod != null && handlerMethod != null)
            {
                var route = GetRouteFromType(type);
                logger.LogInformation("Registered API: {HttpMethod} {Route}", httpMethod.ToUpper(), route);
                var endpointBuilder = RegisterEndpoint(app, type, httpMethod, handlerMethod, app.Services);
                endpointBuilders.Add(endpointBuilder);
            }
        }

        return new CompositeEndpointConventionBuilder(endpointBuilders);
    }

    private static (string? method, MethodInfo? handler) GetHttpMethodAndHandlerFromType(Type type)
    {
        var classHttpAttribute = type.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault();

        if (classHttpAttribute != null)
        {
            var method = classHttpAttribute.Method.ToLowerInvariant();
            var handlerMethod = GetHandlerMethod(type);
            return (method, handlerMethod);
        }

        var methodHttpAttributes = type.GetMethods()
            .Select(m => new { Method = m, Attribute = m.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault() })
            .Where(x => x.Attribute != null)
            .ToList();

        if (methodHttpAttributes.Count == 1)
        {
            var method = methodHttpAttributes[0].Attribute!.Method.ToLowerInvariant();
            return (method, methodHttpAttributes[0].Method);
        }

        return (null, null);
    }


    private static MethodInfo? GetHandlerMethod(Type type)
    {
        return type.GetMethods()
            .FirstOrDefault(m => m.Name.EndsWith("Async") &&
                (m.ReturnType == typeof(Task<IResult>) ||
                 (m.ReturnType.IsGenericType &&
                  m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                  m.ReturnType.GetGenericArguments()[0].Name.StartsWith("Results`"))));
    }
    private static IEndpointConventionBuilder RegisterEndpoint(
       IEndpointRouteBuilder builder,
       Type endpointType,
       string httpMethod,
       MethodInfo handlerMethod,
       IServiceProvider rootProvider)
    {
        var route = GetRouteFromType(endpointType);

        // Try to build a strongly‑typed delegate (needed for Swagger parameter inference)
        Delegate? handlerDelegate = null;
        try
        {
            // Create instance from the *root* provider just to build delegate,
            // we won't actually use this object at runtime if it has scoped deps.
            var tempInstance = Activator.CreateInstance(endpointType);
            if (tempInstance != null)
                handlerDelegate = handlerMethod.CreateDelegate(handlerMethod.ToDelegateType(), tempInstance);
        }
        catch
        {
            // Fallback later for scoped services
            handlerDelegate = null;
        }

        IEndpointConventionBuilder endpointBuilder;

        if (handlerDelegate != null)
        {
            // ✅ This branch gives Swagger real parameter info
            endpointBuilder = builder.MapMethods(route, new[] { httpMethod.ToUpperInvariant() }, handlerDelegate);
        }
        else
        {
            // Fallback when we can't create delegate without DI scope
            // ✅ This branch supports scoped constructor services
            endpointBuilder = builder.MapMethods(route, new[] { httpMethod.ToUpperInvariant() },
                async (HttpContext context) =>
                {
                    await using var scope = rootProvider.CreateAsyncScope();
                    var scopedProvider = scope.ServiceProvider;
                    var instance = ActivatorUtilities.CreateInstance(scopedProvider, endpointType);
                    var args = await BuildParameterArguments(handlerMethod, context, scopedProvider);
                    var result = await (dynamic)handlerMethod.Invoke(instance, args)!;
                    return (IResult)result;
                });
        }

        AddMetadataToEndpoint(endpointBuilder, endpointType, handlerMethod);
        return endpointBuilder;
    }
    private static Type ToDelegateType(this MethodInfo mi)
    {
        var parameterTypes = mi.GetParameters()
            .Select(p => p.ParameterType)
            .ToList();

        parameterTypes.Add(mi.ReturnType);

        // Choose Func<> or Action<> appropriately
        if (mi.ReturnType == typeof(void))
            return Expression.GetActionType(parameterTypes.ToArray());

        return Expression.GetFuncType(parameterTypes.ToArray());
    }
    private static void AddMetadataToEndpoint(IEndpointConventionBuilder endpointBuilder, Type endpointType, MethodInfo handlerMethod)
    {
        var classAttributes = endpointType.GetCustomAttributes();
        var methodAttributes = handlerMethod.GetCustomAttributes();

        foreach (var attribute in classAttributes.Concat(methodAttributes))
        {
            endpointBuilder.WithMetadata(attribute);
        }

        // Add parameter metadata for OpenAPI
        var parameters = handlerMethod.GetParameters();
        foreach (var parameter in parameters)
        {
            var parameterAttributes = parameter.GetCustomAttributes();
            foreach (var attribute in parameterAttributes)
            {
                endpointBuilder.WithMetadata(attribute);
            }
        }

        if (endpointBuilder is RouteHandlerBuilder routeHandlerBuilder)
        {
            // Configure OpenAPI with parameter information
            routeHandlerBuilder.WithOpenApi(operation =>
            {
                var parameters = handlerMethod.GetParameters();
                foreach (var parameter in parameters)
                {
                    var paramName = parameter.Name ?? "";
                    var paramType = parameter.ParameterType;

                    // Skip HttpRequest and other framework types
                    if (paramType == typeof(HttpRequest) ||
                        paramType == typeof(HttpContext) ||
                        paramType == typeof(CancellationToken))
                        continue;

                    // Check if parameter has FromBody attribute
                    var hasFromBody = parameter.GetCustomAttributes(typeof(FromBodyAttribute), false).Any();

                    if (hasFromBody)
                    {
                        // For body parameters, add request body schema
                        operation.RequestBody = new Microsoft.OpenApi.Models.OpenApiRequestBody
                        {
                            Content = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiMediaType>
                            {
                                ["application/json"] = new Microsoft.OpenApi.Models.OpenApiMediaType
                                {
                                    Schema = new Microsoft.OpenApi.Models.OpenApiSchema
                                    {
                                        Type = GetOpenApiType(paramType)
                                    }
                                }
                            }
                        };
                    }
                    else
                    {
                        // For query parameters
                        operation.Parameters ??= new List<Microsoft.OpenApi.Models.OpenApiParameter>();
                        operation.Parameters.Add(new Microsoft.OpenApi.Models.OpenApiParameter
                        {
                            Name = paramName,
                            In = Microsoft.OpenApi.Models.ParameterLocation.Query,
                            Schema = new Microsoft.OpenApi.Models.OpenApiSchema
                            {
                                Type = GetOpenApiType(paramType)
                            }
                        });
                    }
                }

                return operation;
            });
        }
    }

    private static string GetOpenApiType(Type type)
    {
        if (type == typeof(string))
            return "string";
        if (type == typeof(bool))
            return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
            return "integer";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return "number";
        if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            return "array";

        return "object";
    }

    private class CompositeEndpointConventionBuilder : IEndpointConventionBuilder
    {
        private readonly List<IEndpointConventionBuilder> _builders;

        public CompositeEndpointConventionBuilder(List<IEndpointConventionBuilder> builders)
        {
            _builders = builders;
        }

        public void Add(Action<EndpointBuilder> convention)
        {
            foreach (var builder in _builders)
            {
                builder.Add(convention);
            }
        }
    }

    private static string GetRouteFromType(Type endpointType)
    {
        var assembly = endpointType.Assembly;
        var assemblyLocation = assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);

        if (assemblyDirectory == null)
            throw new InvalidOperationException("Could not determine assembly directory");

        var projectRoot = FindProjectRoot(assemblyDirectory);
        var typeLocation = GetTypeLocation(endpointType);

        if (typeLocation == null)
            throw new InvalidOperationException($"Could not determine file location for type {endpointType.FullName}");

        var relativePath = Path.GetRelativePath(projectRoot, typeLocation);

        var hasClassAttribute = endpointType.GetCustomAttributes<HttpMethodAttribute>().Any();

        return ConvertFilePathToRoute(relativePath, !hasClassAttribute).ToLowerInvariant();
    }

    private static string FindProjectRoot(string assemblyDirectory)
    {
        var directory = assemblyDirectory;
        while (directory != null)
        {
            var csprojFiles = Directory.GetFiles(directory, "*.csproj");
            if (csprojFiles.Length > 0)
                return directory;

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not find project root directory");
    }

    private static string? GetTypeLocation(Type type)
    {
        var assembly = type.Assembly;
        var assemblyLocation = assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);

        if (assemblyDirectory == null)
            return null;

        var projectRoot = FindProjectRoot(assemblyDirectory);

        var searchPattern = $"{type.Name}.cs";
        var files = Directory.GetFiles(projectRoot, searchPattern, SearchOption.AllDirectories);

        var matchingFiles = files.Where(f => Path.GetFileNameWithoutExtension(f) == type.Name).ToList();

        if (matchingFiles.Count == 1)
            return matchingFiles[0];

        if (matchingFiles.Count > 1)
        {
            var typeNamespace = type.Namespace ?? "";
            var expectedPath = typeNamespace.Replace('.', Path.DirectorySeparatorChar);

            return matchingFiles.FirstOrDefault(f => f.Contains(expectedPath));
        }

        return null;
    }

    private static string ConvertFilePathToRoute(string filePath, bool includeFileName = true)
    {
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        if (directory == null)
            return $"/{fileName.ToLowerInvariant()}";

        var routePath = directory.Replace(Path.DirectorySeparatorChar, '/');

        if (routePath.StartsWith("Api/") || routePath.StartsWith("api/"))
        {
            routePath = routePath.Substring(4);
        }

        var route = includeFileName
            ? $"/api/{routePath}/{fileName}".Replace("//", "/")
            : $"/api/{routePath}".Replace("//", "/");

        return route.ToLowerInvariant();
    }

    private static async Task<object[]> BuildParameterArguments(MethodInfo method, HttpContext context, IServiceProvider provider)
    {
        var parameters = method.GetParameters();
        var args = new object[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            args[i] = await GetParameterValue(param, context, provider, method);
        }

        return args;
    }

    private static async Task<object> GetParameterValue(ParameterInfo param, HttpContext context, IServiceProvider provider, MethodInfo method)
    {
        var paramType = param.ParameterType;
        var paramName = param.Name ?? "";

        // Check if this parameter has [FromBody] attribute
        var hasFromBody = param.GetCustomAttributes(typeof(FromBodyAttribute), false).Any();

        // For backward compatibility, also check method-level [FromBody]
        var methodHasFromBody = method.GetCustomAttributes(typeof(FromBodyAttribute), false).Any();

        if (hasFromBody || methodHasFromBody)
        {
            if (context.Request.HasJsonContentType() &&
                (context.Request.Method == "POST" || context.Request.Method == "PUT" || context.Request.Method == "PATCH"))
            {
                try
                {
                    // Get or create cached body
                    var body = await GetCachedRequestBody(context, provider);
                    if (body != null && body.TryGetValue(paramName, out var property))
                    {
                        return ConvertJsonElementToType(property, paramType, param);
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    // Fall through to other binding methods
                }
            }
        }

        // Fall back to URL parameter binding
        if (paramType == typeof(string))
        {
            var value = context.Request.Query[paramName].FirstOrDefault();
            return value ?? (param.HasDefaultValue ? param.DefaultValue! : null!);
        }

        if (paramType == typeof(bool))
        {
            var value = context.Request.Query[paramName].FirstOrDefault();
            return bool.TryParse(value, out var boolValue) ? boolValue : (param.HasDefaultValue ? param.DefaultValue! : false);
        }

        if (paramType == typeof(string[]))
        {
            var values = context.Request.Query[paramName];
            return values.Count > 0 ? values.ToArray() : Array.Empty<string>();
        }

        if (paramType == typeof(HttpRequest))
        {
            return context.Request;
        }

        return provider.GetRequiredService(paramType);
    }

    private static async Task<Dictionary<string, JsonElement>?> GetCachedRequestBody(HttpContext context, IServiceProvider provider)
    {
        const string cacheKey = "__CachedRequestBody__";

        if (context.Items.TryGetValue(cacheKey, out var cachedBody))
        {
            return cachedBody as Dictionary<string, JsonElement>;
        }

        context.Request.EnableBuffering();

        // Read the raw body as text
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var bodyText = await reader.ReadToEndAsync();

        // Reset position so ReadFromJsonAsync can read it again
        context.Request.Body.Position = 0;

        // Deserialize entire JSON body
        var jsonOptions = provider.GetService<JsonOptions>();
        var body = await context.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>(jsonOptions?.SerializerOptions);

        // Cache the body for subsequent parameter bindings
        context.Items[cacheKey] = body;

        return body;
    }

    private static object ConvertJsonElementToType(JsonElement property, Type paramType, ParameterInfo param)
    {
        if (paramType == typeof(string) && property.ValueKind == JsonValueKind.String)
            return property.GetString() ?? (param.HasDefaultValue ? param.DefaultValue! : null!);

        if (paramType == typeof(bool) && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
            return property.GetBoolean();

        if (paramType == typeof(string[]) && property.ValueKind == JsonValueKind.Array)
        {
            var arr = property.EnumerateArray();
            var mid = arr.Select(e => e.GetString() ?? string.Empty);
            return mid.ToArray();
        }

        // For other types, try to deserialize directly
        try
        {
            return JsonSerializer.Deserialize(property.GetRawText(), paramType) ??
                   (param.HasDefaultValue ? param.DefaultValue! : null!);
        }
        catch
        {
            return param.HasDefaultValue ? param.DefaultValue! : null!;
        }
    }
}
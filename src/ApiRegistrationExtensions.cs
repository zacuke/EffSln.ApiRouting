using System.Reflection;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EffSln.ApiRouting;

public static class ApiRegistrationExtensions
{
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

    public static RouteGroupBuilder MapApiEndpoints(this RouteGroupBuilder group, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiRegistrationExtensions));

        var endpointTypes = Assembly.GetCallingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract &&
                (t.GetCustomAttributes<HttpMethodAttribute>().Any() ||
                 t.GetMethods().Any(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())));

        foreach (var type in endpointTypes)
        {
            var (httpMethod, handlerMethod) = GetHttpMethodAndHandlerFromType(type);

            if (httpMethod != null && handlerMethod != null)
            {
                var route = GetRouteFromType(type);
                logger.LogInformation("Registered API: {HttpMethod} {Route}", httpMethod.ToUpper(), route);
                RegisterEndpoint(group, type, httpMethod, handlerMethod, serviceProvider);
            }
        }

        return group;
    }

    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiRegistrationExtensions));

        var endpointTypes = Assembly.GetCallingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract &&
                (t.GetCustomAttributes<HttpMethodAttribute>().Any() ||
                 t.GetMethods().Any(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())));

        foreach (var type in endpointTypes)
        {
            var (httpMethod, handlerMethod) = GetHttpMethodAndHandlerFromType(type);

            if (httpMethod != null && handlerMethod != null)
            {
                var route = GetRouteFromType(type);
                logger.LogInformation("Registered API: {HttpMethod} {Route}", httpMethod.ToUpper(), route);
                RegisterEndpoint(app, type, httpMethod, handlerMethod, app.Services);
            }
        }

        return app;
    }

    private static (string? method, MethodInfo? handler) GetHttpMethodAndHandlerFromType(Type type)
    {
        var classHttpAttribute = type.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault();

        if (classHttpAttribute != null)
        {
            var method = GetMethodFromAttribute(classHttpAttribute);
            var handlerMethod = GetHandlerMethod(type);
            return (method, handlerMethod);
        }

        var methodHttpAttributes = type.GetMethods()
            .Select(m => new { Method = m, Attribute = m.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault() })
            .Where(x => x.Attribute != null)
            .ToList();

        if (methodHttpAttributes.Count == 1)
        {
            var method = GetMethodFromAttribute(methodHttpAttributes[0].Attribute!);
            return (method, methodHttpAttributes[0].Method);
        }

        return (null, null);
    }

    private static string? GetMethodFromAttribute(Attribute attribute)
    {
        var attributeName = attribute.GetType().Name;
        return attributeName switch
        {
            "HttpGetAttribute" => "get",
            "HttpPostAttribute" => "post",
            "HttpPutAttribute" => "put",
            "HttpDeleteAttribute" => "delete",
            "HttpPatchAttribute" => "patch",
            "HttpOptionsAttribute" => "options",
            "HttpHeadAttribute" => "head",
            _ => null
        };
    }

    private static MethodInfo? GetHandlerMethod(Type type)
    {
        return type.GetMethods()
            .FirstOrDefault(m => m.Name.EndsWith("Async") && m.ReturnType == typeof(Task<IResult>));
    }

    private static void RegisterEndpoint(IEndpointRouteBuilder builder, Type endpointType, string httpMethod, MethodInfo handlerMethod, IServiceProvider serviceProvider)
    {
        var route = GetRouteFromType(endpointType);

        var routeHandler = async (HttpContext context) =>
        {
            using var scope = serviceProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;
            var handlerInstance = scopedProvider.GetRequiredService(endpointType);
            var args = BuildParameterArguments(handlerMethod, context, scopedProvider);
            return await (Task<IResult>)handlerMethod.Invoke(handlerInstance, args)!;
        };

        switch (httpMethod)
        {
            case "get":
                builder.MapGet(route, routeHandler);
                break;
            case "post":
                builder.MapPost(route, routeHandler);
                break;
            case "put":
                builder.MapPut(route, routeHandler);
                break;
            case "delete":
                builder.MapDelete(route, routeHandler);
                break;
            case "patch":
                builder.MapPatch(route, routeHandler);
                break;
            case "options":
                builder.MapMethods(route, new[] { "OPTIONS" }, routeHandler);
                break;
            case "head":
                builder.MapMethods(route, new[] { "HEAD" }, routeHandler);
                break;
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

    private static object[] BuildParameterArguments(MethodInfo method, HttpContext context, IServiceProvider provider)
    {
        var parameters = method.GetParameters();
        var args = new object[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            args[i] = GetParameterValue(param, context, provider);
        }

        return args;
    }

    private static object GetParameterValue(ParameterInfo param, HttpContext context, IServiceProvider provider)
    {
        var paramType = param.ParameterType;
        var paramName = param.Name ?? "";

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
            return context.Request.Query[paramName].ToArray();
        }

        if (paramType == typeof(HttpRequest))
        {
            return context.Request;
        }

        return provider.GetRequiredService(paramType);
    }
}
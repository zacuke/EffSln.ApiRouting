namespace EffSln.ApiRouting;
/// <summary>
/// Base attribute for HTTP method attributes used to mark classes and methods for automatic API endpoint registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public abstract class HttpMethodAttribute : Attribute
{
    /// <summary>
    /// Gets the HTTP method name for the attribute.
    /// </summary>
    public abstract string Method { get; }
}

/// <summary>
/// Attribute for HTTP GET method endpoints.
/// </summary>
public class HttpGetAttribute : HttpMethodAttribute
{
    /// <summary>
    /// Gets the HTTP method name "GET".
    /// </summary>
    public override string Method => "GET";
}

/// <summary>
/// Attribute for HTTP POST method endpoints.
/// </summary>
public class HttpPostAttribute : HttpMethodAttribute
{
    /// <summary>
    /// Gets the HTTP method name "POST".
    /// </summary>
    public override string Method => "POST";
}

/// <summary>
/// Attribute for HTTP PUT method endpoints.
/// </summary>
public class HttpPutAttribute : HttpMethodAttribute
{
    /// <summary>
    /// Gets the HTTP method name "PUT".
    /// </summary>
    public override string Method => "PUT";
}

/// <summary>
/// Attribute for HTTP DELETE method endpoints.
/// </summary>
public class HttpDeleteAttribute : HttpMethodAttribute
{
    /// <summary>
    /// Gets the HTTP method name "DELETE".
    /// </summary>
    public override string Method => "DELETE";
}
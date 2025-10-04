# EffSln.ApiRouting

Automatic API endpoint registration for ASP.NET Core.

## Usage

```csharp
// Program.cs
builder.Services.AddApiEndpoints();
app.MapApiEndpoints();
```

## Endpoint Examples

```csharp
// Api/Products/Get.cs
public class Get
{
    [HttpGet]
    public async Task<IResult> HandleAsync(string category)
    {
        return Results.Ok(new { category });
    }
}
```

```csharp
// Api/Webhooks/Stripe.cs
[HttpPost]
public class Stripe
{
    public async Task<IResult> PostAsync(HttpRequest request)
    {
        return Results.Ok();
    }
}
```

## Routes

- `Api/Products/Get.cs` → `/api/products/get`
- `Api/Users/Create.cs` → `/api/users/create`
- `Api/Webhooks/Stripe.cs` → `/api/webhooks/stripe`

## Parameters

```csharp
public async Task<IResult> HandleAsync(
    string queryParam,     // From query string
    bool flag,             // From query string
    string[] items,        // From query string
    IMyService service,    // From DI
    HttpRequest request    // Direct access
)
```

## Requirements

- Return `Task<IResult>`
- Method names ending with "Async"
- HTTP method attributes (`[HttpGet]`, `[HttpPost]`, etc.)
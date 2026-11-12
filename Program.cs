using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using RnxRagService;

var builder = WebApplication.CreateBuilder(args);
var ragOptions = builder.Configuration.GetSection("Rag").Get<RagOptions>() ?? new RagOptions();
ragOptions.ChatModel = ragOptions.ChatModel.Trim();
ragOptions.EmbeddingModel = ragOptions.EmbeddingModel.Trim();
if (string.IsNullOrWhiteSpace(ragOptions.DatabasePath) ||
    !Uri.TryCreate(ragOptions.OllamaUrl.TrimEnd('/') + "/", UriKind.Absolute, out var ollamaUri) ||
    ollamaUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(ollamaUri.Query + ollamaUri.Fragment) ||
    ragOptions.MinSimilarity is < 0 or > 1 || !double.IsFinite(ragOptions.MinSimilarity))
{
    throw new InvalidOperationException("Проверьте настройки Rag:DatabasePath, Rag:OllamaUrl и Rag:MinSimilarity.");
}

const int maxRequestBytes = DocumentReader.MaxBytes + 64 * 1024;
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = maxRequestBytes);
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = maxRequestBytes);
builder.Services.Configure<RouteHandlerOptions>(routes => routes.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(ragOptions);
builder.Services.AddSingleton(_ => new RagStore(Path.GetFullPath(ragOptions.DatabasePath, builder.Environment.ContentRootPath)));
builder.Services.AddHttpClient<OllamaClient>(http =>
{
    http.BaseAddress = ollamaUri;
    http.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddTransient<RagEngine>();

var app = builder.Build();
_ = app.Services.GetRequiredService<RagStore>();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (RagInputException input) when (!context.Response.HasStarted)
    {
        await Results.Problem(statusCode: input.StatusCode, detail: input.Message).ExecuteAsync(context);
    }
    catch (BadHttpRequestException invalid) when (!context.Response.HasStarted)
    {
        var detail = invalid.StatusCode == 413
            ? "Запрос превышает допустимый размер."
            : "Проверьте формат запроса и его параметры.";
        await Results.Problem(statusCode: invalid.StatusCode, detail: detail).ExecuteAsync(context);
    }
});
app.UseStatusCodePages();

app.Use(async (context, next) =>
{
    var request = context.Request;
    if (request.Path.StartsWithSegments("/api") && !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
    {
        var origin = request.Headers.Origin.ToString();
        var expectedOrigin = $"{request.Scheme}://{request.Host}";
        if ((origin.Length > 0 && !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase)) ||
            request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            await Results.Problem(statusCode: 403, detail: "Запрос с другого сайта отклонён.").ExecuteAsync(context);
            return;
        }
    }

    await next(context);
});

app.UseDefaultFiles();
var fileTypes = new FileExtensionContentTypeProvider();
fileTypes.Mappings[".md"] = "text/markdown; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = fileTypes });
var api = app.MapGroup("/api");
api.MapGet("/health", (RagOptions options) => Results.Ok(new
{
    status = "ok",
    chatModel = options.UseChat ? options.ChatModel : null,
    embeddingModel = options.UseEmbeddings ? options.EmbeddingModel : null
}));
api.MapGet("/documents", (RagStore store) => Results.Ok(store.ListDocuments()));
api.MapGet("/documents/{id}", (string id, RagStore store) =>
    store.GetDocument(id) is { } document ? Results.Ok(document) : MissingDocument());
api.MapPost("/documents", async (IFormFile file, RagEngine rag, CancellationToken cancellationToken) =>
{
    var (document, created) = await rag.UploadAsync(file, cancellationToken);
    return created ? Results.Created($"/api/documents/{document.Id}", document) : Results.Ok(document);
}).DisableAntiforgery();
api.MapDelete("/documents/{id}", (string id, RagStore store) =>
    store.DeleteDocument(id) ? Results.NoContent() : MissingDocument());
api.MapPost("/documents/{id}/reindex", async (string id, RagEngine rag, CancellationToken cancellationToken) =>
    await rag.ReindexAsync(id, cancellationToken) ? Results.NoContent() : MissingDocument());
api.MapGet("/documents/{id}/chunks/{chunk:int}", (string id, int chunk, RagStore store) =>
    store.GetPassage(id, chunk) is { } hit
        ? Results.Ok(new { hit.DocumentId, hit.DocumentName, chunk, hit.Passage.Page, hit.Passage.Text })
        : Results.Problem(statusCode: 404, detail: "Фрагмент не найден."));
api.MapPost("/ask", async (AskRequest request, RagEngine rag, CancellationToken cancellationToken) =>
    Results.Ok(await rag.AskAsync(request, cancellationToken)))
    .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

app.Run();

static IResult MissingDocument() => Results.Problem(statusCode: 404, detail: "Документ не найден.");

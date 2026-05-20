using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GhProxy.Api.Contracts;
using GhProxy.Api.Domain;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class GitHubApiException(HttpStatusCode statusCode, string message, string? body = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Body { get; } = body;
}

public sealed record GitHubUserProfile(string Login);

public sealed record GitHubCodespaceExportRemote(
    string? Id,
    string? State,
    string? ExportUrl,
    string? HtmlUrl,
    DateTimeOffset? CompletedAt);

public sealed record GitHubCodespaceRemote(
    string Name,
    string State,
    string? RepositoryFullName,
    string? MachineDisplayName,
    string? Location,
    string? WebUrl,
    string? BillableOwner,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastUsedAt);

public interface IGitHubApiClient
{
    Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken);
    Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken);
    Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken);
}

public sealed class GitHubApiClient(
    HttpClient httpClient,
    IOptions<GitHubOptions> options,
    IOperationalEventSink events,
    ISensitiveDataRedactor redactor,
    ILogger<GitHubApiClient> logger) : IGitHubApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GitHubOptions _options = options.Value;

    public async Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(token, HttpMethod.Get, "user", null, "github.user.get", cancellationToken);
        return new GitHubUserProfile(GetString(document.RootElement, "login") ?? string.Empty);
    }

    public async Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken)
    {
        var codespaces = new List<GitHubCodespaceRemote>();
        for (var page = 1; page <= 10; page++)
        {
            using var document = await SendAsync(token, HttpMethod.Get, $"user/codespaces?per_page=100&page={page}", null, "github.codespaces.list", cancellationToken);
            if (!document.RootElement.TryGetProperty("codespaces", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return codespaces;
            }

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                codespaces.Add(ToCodespace(item));
            }

            if (count < 100)
            {
                break;
            }
        }

        return codespaces;
    }

    public async Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>();
        AddIfSet(payload, "ref", request.Ref);
        AddIfSet(payload, "geo", request.Geo);
        AddIfSet(payload, "machine", request.Machine);
        AddIfSet(payload, "display_name", request.DisplayName);
        if (request.IdleTimeoutMinutes is not null)
        {
            payload["idle_timeout_minutes"] = request.IdleTimeoutMinutes.Value;
        }

        using var document = await SendAsync(
            token,
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(request.RepositoryOwner.Trim())}/{Uri.EscapeDataString(request.RepositoryName.Trim())}/codespaces",
            payload,
            "github.codespaces.create",
            cancellationToken);
        return ToCodespace(document.RootElement);
    }

    public async Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(token, HttpMethod.Post, $"user/codespaces/{Uri.EscapeDataString(codespaceName)}/start", null, "github.codespaces.start", cancellationToken);
        return ToCodespace(document.RootElement);
    }

    public async Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(token, HttpMethod.Post, $"user/codespaces/{Uri.EscapeDataString(codespaceName)}/stop", null, "github.codespaces.stop", cancellationToken);
        return ToCodespace(document.RootElement);
    }

    public async Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
    {
        await SendNoContentAsync(token, HttpMethod.Delete, $"user/codespaces/{Uri.EscapeDataString(codespaceName)}", "github.codespaces.delete", cancellationToken);
    }

    public async Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(token, HttpMethod.Post, $"user/codespaces/{Uri.EscapeDataString(codespaceName)}/exports", null, "github.codespaces.export", cancellationToken);
        return ToExport(document.RootElement);
    }

    public async Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken)
    {
        var path = $"users/{Uri.EscapeDataString(username)}/settings/billing/usage/summary?product=Codespaces";
        try
        {
            using var document = await SendAsync(token, HttpMethod.Get, path, null, "github.billing.usage", cancellationToken);
            var usageItems = document.RootElement.TryGetProperty("usageItems", out var camelItems)
                ? camelItems
                : document.RootElement.TryGetProperty("usage_items", out var snakeItems)
                    ? snakeItems
                    : default;

            decimal? quantity = null;
            decimal? netAmount = null;
            string? unitType = null;
            if (usageItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in usageItems.EnumerateArray())
                {
                    if (!ContainsCodespaces(GetString(item, "product")))
                    {
                        continue;
                    }

                    quantity = (quantity ?? 0) + (GetDecimal(item, "netQuantity") ?? GetDecimal(item, "grossQuantity") ?? GetDecimal(item, "quantity") ?? 0);
                    netAmount = (netAmount ?? 0) + (GetDecimal(item, "netAmount") ?? GetDecimal(item, "net_amount") ?? 0);
                    unitType ??= GetString(item, "unitType") ?? GetString(item, "unit_type");
                }
            }

            return new GitHubUsageResponse(
                GitHubAccountQuotaState.Healthy,
                quantity is null ? "Usage endpoint is reachable, but no Codespaces items were returned." : "Usage endpoint is reachable.",
                quantity,
                unitType,
                netAmount,
                $"https://github.com/settings/billing/usage?query=product%3ACodespaces");
        }
        catch (GitHubApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return new GitHubUsageResponse(
                GitHubAccountQuotaState.Unavailable,
                "Codespaces billing usage is unavailable for this token/account.",
                null,
                null,
                null,
                "https://github.com/settings/billing/usage?query=product%3ACodespaces");
        }
    }

    private async Task<JsonDocument> SendAsync(string token, HttpMethod method, string path, object? body, string eventType, CancellationToken cancellationToken)
    {
        var response = await SendCoreAsync(token, method, path, body, eventType, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await WriteFailureAsync(eventType, response, content, cancellationToken);
            throw new GitHubApiException(response.StatusCode, $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.", content);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            content = "{}";
        }

        await WriteSuccessAsync(eventType, response, cancellationToken);
        return JsonDocument.Parse(content);
    }

    private async Task SendNoContentAsync(string token, HttpMethod method, string path, string eventType, CancellationToken cancellationToken)
    {
        var response = await SendCoreAsync(token, method, path, null, eventType, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await WriteFailureAsync(eventType, response, content, cancellationToken);
            throw new GitHubApiException(response.StatusCode, $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.", content);
        }

        await WriteSuccessAsync(eventType, response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(string token, HttpMethod method, string path, object? body, string eventType, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _options.ApiVersion);
        request.Headers.UserAgent.ParseAdd("gh-proxy-codespaces-manager/1.0");
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        await events.WriteAsync(new OperationalEventWrite(
            eventType,
            OperationalEventSeverity.Information,
            $"Calling GitHub API {method} {path}.",
            CommandKind: "github-api",
            CommandDisplay: $"{method} {path}"), cancellationToken);

        try
        {
            var response = await httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();
            logger.LogInformation("GitHub API {Method} {Path} returned {StatusCode} in {ElapsedMs}ms.", method, path, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            await events.WriteAsync(new OperationalEventWrite(
                eventType,
                OperationalEventSeverity.Error,
                $"GitHub API request failed: {redactor.Redact(ex.Message)}",
                CommandKind: "github-api",
                CommandDisplay: $"{method} {path}",
                Duration: stopwatch.Elapsed,
                StandardError: ex.ToString()), cancellationToken);
            throw;
        }
    }

    private Task WriteSuccessAsync(string eventType, HttpResponseMessage response, CancellationToken cancellationToken) =>
        events.WriteAsync(new OperationalEventWrite(
            eventType,
            OperationalEventSeverity.Information,
            $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
            CommandKind: "github-api",
            CommandDisplay: $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}",
            ExitCode: (int)response.StatusCode),
            cancellationToken);

    private Task WriteFailureAsync(string eventType, HttpResponseMessage response, string body, CancellationToken cancellationToken) =>
        events.WriteAsync(new OperationalEventWrite(
            eventType,
            OperationalEventSeverity.Warning,
            $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
            CommandKind: "github-api",
            CommandDisplay: $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}",
            ExitCode: (int)response.StatusCode,
            StandardError: body),
            cancellationToken);

    private static GitHubCodespaceRemote ToCodespace(JsonElement element)
    {
        var machine = element.TryGetProperty("machine", out var machineElement)
            ? GetString(machineElement, "display_name") ?? GetString(machineElement, "name")
            : null;

        return new GitHubCodespaceRemote(
            GetString(element, "name") ?? string.Empty,
            GetString(element, "state") ?? "Unknown",
            GetNestedString(element, "repository", "full_name"),
            machine,
            GetString(element, "location") ?? GetString(element, "geo"),
            GetString(element, "web_url"),
            GetNestedString(element, "billable_owner", "login"),
            GetDate(element, "created_at"),
            GetDate(element, "updated_at"),
            GetDate(element, "last_used_at"));
    }

    private static GitHubCodespaceExportRemote ToExport(JsonElement element) =>
        new(
            GetString(element, "id"),
            GetString(element, "state"),
            GetString(element, "export_url"),
            GetString(element, "html_url"),
            GetDate(element, "completed_at"));

    private static void AddIfSet(Dictionary<string, object?> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value.Trim();
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetNestedString(JsonElement element, string property, string nestedProperty) =>
        element.TryGetProperty(property, out var value) ? GetString(value, nestedProperty) : null;

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        DateTimeOffset.TryParse(GetString(element, name), out var value) ? value : null;

    private static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static bool ContainsCodespaces(string? value) =>
        value?.Contains("codespaces", StringComparison.OrdinalIgnoreCase) == true;
}

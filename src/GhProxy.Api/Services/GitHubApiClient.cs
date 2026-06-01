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

public sealed record GitHubUserProfile(string Login, string? Name, string? PlanName);

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
    Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken);
    Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken);
    Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken);
    Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken);
    Task<GitHubCodespaceExportRemote?> GetLatestCodespaceExportAsync(string token, string codespaceName, CancellationToken cancellationToken);
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
        var planName = document.RootElement.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object
            ? GetString(plan, "name")
            : null;
        return new GitHubUserProfile(
            GetString(document.RootElement, "login") ?? string.Empty,
            GetString(document.RootElement, "name"),
            planName);
    }

    public async Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await SendAsync(
                token,
                HttpMethod.Get,
                $"repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repository.Trim())}",
                null,
                "github.repository.get",
                cancellationToken);
            return true;
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["default_branch_only"] = true };
        var response = await SendCoreAsync(
            token,
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repository.Trim())}/forks",
            payload,
            "github.repository.fork",
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await WriteSuccessAsync("github.repository.fork", response, cancellationToken);
            return;
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity &&
            content.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSuccessAsync("github.repository.fork", response, cancellationToken);
            return;
        }

        await WriteFailureAsync("github.repository.fork", response, content, cancellationToken);
        throw new GitHubApiException(response.StatusCode, $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.", content);
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

    public async Task<GitHubCodespaceExportRemote?> GetLatestCodespaceExportAsync(string token, string codespaceName, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await SendAsync(token, HttpMethod.Get, $"user/codespaces/{Uri.EscapeDataString(codespaceName)}/exports/latest", null, "github.codespaces.export.latest", cancellationToken);
            return ToExport(document.RootElement);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
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

            var period = GetBillingPeriod(document.RootElement);
            var compute = new UsageAccumulator("Compute", "included units");
            var storage = new UsageAccumulator("Storage", "GB-month");
            decimal? quantity = null;
            decimal? netAmount = null;
            string? unitType = null;
            if (usageItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in usageItems.EnumerateArray())
                {
                    var product = GetString(item, "product");
                    var sku = GetString(item, "sku") ?? GetString(item, "skuName") ?? GetString(item, "sku_name");
                    if (!ContainsCodespaces(product) && !ContainsCodespaces(sku))
                    {
                        continue;
                    }

                    var grossQuantity = GetDecimal(item, "grossQuantity") ?? GetDecimal(item, "quantity") ?? GetDecimal(item, "netQuantity") ?? 0;
                    quantity = (quantity ?? 0) + grossQuantity;
                    netAmount = (netAmount ?? 0) + (GetDecimal(item, "netAmount") ?? GetDecimal(item, "net_amount") ?? 0);
                    unitType ??= GetString(item, "unitType") ?? GetString(item, "unit_type");
                    var itemUnit = GetString(item, "unitType") ?? GetString(item, "unit_type");
                    if (IsCodespacesComputeSku(sku, product))
                    {
                        compute.Add(grossQuantity * GetComputeUnitMultiplier(sku), "included units");
                    }
                    else if (IsCodespacesStorageSku(sku, product))
                    {
                        storage.Add(ToGbMonths(grossQuantity, period.Year, period.Month), "GB-month");
                    }
                }
            }

            var quotas = new List<GitHubUsageQuotaSummaryResponse>();
            if (compute.HasUsage)
            {
                quotas.Add(compute.ToResponse());
            }

            if (storage.HasUsage)
            {
                quotas.Add(storage.ToResponse());
            }

            return new GitHubUsageResponse(
                GitHubAccountQuotaState.Healthy,
                quantity is null ? "Usage endpoint is reachable, but no Codespaces items were returned." : "Usage endpoint is reachable.",
                quantity,
                unitType,
                netAmount,
                $"https://github.com/settings/billing/usage?query=product%3ACodespaces",
                quotas);
        }
        catch (GitHubApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return new GitHubUsageResponse(
                GitHubAccountQuotaState.Unavailable,
                "Codespaces billing usage is unavailable for this token/account.",
                null,
                null,
                null,
                "https://github.com/settings/billing/usage?query=product%3ACodespaces",
                []);
        }
    }

    private async Task<JsonDocument> SendAsync(string token, HttpMethod method, string path, object? body, string eventType, CancellationToken cancellationToken)
    {
        var response = await SendCoreAsync(token, method, path, body, eventType, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await WriteFailureAsync(eventType, response, content, cancellationToken);
            throw new GitHubApiException(response.StatusCode, BuildGitHubErrorMessage(response, content), content);
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
            throw new GitHubApiException(response.StatusCode, BuildGitHubErrorMessage(response, content), content);
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

    private static string BuildGitHubErrorMessage(HttpResponseMessage response, string content)
    {
        var statusText = $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (string.IsNullOrWhiteSpace(content))
        {
            return statusText;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var details = new List<string>();
            var message = GetString(root, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                details.Add(message);
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray().Take(3))
                {
                    var errorMessage = GetString(error, "message");
                    var code = GetString(error, "code");
                    var field = GetString(error, "field");
                    var resource = GetString(error, "resource");
                    var parts = new[] { resource, field, code, errorMessage }.Where(x => !string.IsNullOrWhiteSpace(x));
                    var detail = string.Join(" ", parts);
                    if (!string.IsNullOrWhiteSpace(detail))
                    {
                        details.Add(detail);
                    }
                }
            }

            return details.Count == 0 ? statusText : $"{statusText} {string.Join(" ", details)}";
        }
        catch (JsonException)
        {
            return statusText;
        }
    }

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

    private static bool IsCodespacesComputeSku(string? sku, string? product) =>
        sku?.StartsWith("codespaces_compute_", StringComparison.OrdinalIgnoreCase) == true ||
        product?.Contains("compute", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCodespacesStorageSku(string? sku, string? product) =>
        sku?.Equals("codespaces_storage", StringComparison.OrdinalIgnoreCase) == true ||
        sku?.Equals("codespaces_prebuild_storage", StringComparison.OrdinalIgnoreCase) == true ||
        product?.Contains("storage", StringComparison.OrdinalIgnoreCase) == true;

    private static decimal GetComputeUnitMultiplier(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return 1;
        }

        var suffix = sku[(sku.LastIndexOf('_') + 1)..];
        var digits = new string(suffix.Where(char.IsDigit).ToArray());
        return decimal.TryParse(digits, out var cores) && cores > 0 ? cores : 1;
    }

    private static decimal ToGbMonths(decimal gbHours, int year, int month)
    {
        var hoursInMonth = DateTime.DaysInMonth(year, month) * 24;
        return hoursInMonth <= 0 ? 0 : gbHours / hoursInMonth;
    }

    private static (int Year, int Month) GetBillingPeriod(JsonElement root)
    {
        var now = DateTimeOffset.UtcNow;
        if (!root.TryGetProperty("timePeriod", out var period))
        {
            return (now.Year, now.Month);
        }

        var year = GetInt(period, "year") ?? now.Year;
        var month = GetInt(period, "month") ?? now.Month;
        return month is >= 1 and <= 12 ? (year, month) : (now.Year, now.Month);
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private sealed class UsageAccumulator(string name, string fallbackUnit)
    {
        public decimal Used { get; private set; }
        public string Unit { get; private set; } = fallbackUnit;
        public bool HasUsage { get; private set; }

        public void Add(decimal quantity, string? unit)
        {
            Used += quantity;
            if (!string.IsNullOrWhiteSpace(unit))
            {
                Unit = NormalizeUnit(unit);
            }

            HasUsage = true;
        }

        public GitHubUsageQuotaSummaryResponse ToResponse() =>
            new(name, Used, null, null, null, Unit);
    }

    private static string NormalizeUnit(string unit) =>
        unit.Trim().ToLowerInvariant() switch
        {
            "hours" or "hour" or "hrs" or "hr" => "core hours",
            "gb_month" or "gb-month" or "gb month" or "gb-months" or "gb months" => "GB-month",
            _ => unit.Trim()
        };
}

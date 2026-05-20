using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TradingBot.Application;
using TradingBot.Shared;

namespace TradingBot.Infrastructure.Broker;

public sealed class CTraderConnectionTester(TradingBotOptions options)
{
    public string BuildAuthorizationUrl()
    {
        var cTrader = options.CTrader;
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = cTrader.ClientId,
            ["redirect_uri"] = cTrader.RedirectUri,
            ["scope"] = cTrader.Scope,
            ["product"] = "web"
        };

        return $"{cTrader.AuthorizationEndpoint}?{BuildQueryString(query)}";
    }

    public async Task<Result<CTraderTokenTestResult>> TestTokenEndpointAsync(CancellationToken cancellationToken)
    {
        var cTrader = options.CTrader;
        var validation = ValidateRequiredSettings();
        if (!validation.IsSuccess)
        {
            return Result<CTraderTokenTestResult>.Failure(validation.Error!);
        }

        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = cTrader.ClientId,
            ["client_secret"] = cTrader.ClientSecret
        };

        if (!string.IsNullOrWhiteSpace(cTrader.RefreshToken))
        {
            parameters["grant_type"] = "refresh_token";
            parameters["refresh_token"] = cTrader.RefreshToken;
        }
        else if (!string.IsNullOrWhiteSpace(cTrader.AuthorizationCode))
        {
            if (string.IsNullOrWhiteSpace(cTrader.RedirectUri))
            {
                return Result<CTraderTokenTestResult>.Failure("CTrader:RedirectUri is required when exchanging AuthorizationCode.");
            }

            parameters["grant_type"] = "authorization_code";
            parameters["code"] = cTrader.AuthorizationCode;
            parameters["redirect_uri"] = cTrader.RedirectUri;
        }
        else if (!string.IsNullOrWhiteSpace(cTrader.AccessToken))
        {
            return Result<CTraderTokenTestResult>.Success(new CTraderTokenTestResult(
                true,
                "AccessToken is configured. Token endpoint was not called because AuthorizationCode or RefreshToken was not provided.",
                HasAccessToken: true,
                HasRefreshToken: !string.IsNullOrWhiteSpace(cTrader.RefreshToken),
                ExpiresInSeconds: null,
                AccessToken: null,
                RefreshToken: null));
        }
        else
        {
            return Result<CTraderTokenTestResult>.Failure("Provide CTrader:AuthorizationCode or CTrader:RefreshToken to test the cTrader OAuth endpoint.");
        }

        var tokenResponse = await RequestTokenAsync(parameters, cancellationToken);
        if (!tokenResponse.IsSuccess)
        {
            return Result<CTraderTokenTestResult>.Failure(tokenResponse.Error!);
        }

        return Result<CTraderTokenTestResult>.Success(new CTraderTokenTestResult(
            true,
            "cTrader OAuth token endpoint responded successfully.",
            HasAccessToken: true,
            HasRefreshToken: !string.IsNullOrWhiteSpace(tokenResponse.Value!.RefreshToken),
            tokenResponse.Value.ExpiresIn,
            tokenResponse.Value.AccessToken,
            tokenResponse.Value.RefreshToken));
    }

    public async Task<Result<CTraderTokenExchangeResult>> ExchangeAuthorizationCodeAsync(string authorizationCode, CancellationToken cancellationToken)
    {
        var validation = ValidateRequiredSettings();
        if (!validation.IsSuccess)
        {
            return Result<CTraderTokenExchangeResult>.Failure(validation.Error!);
        }

        if (string.IsNullOrWhiteSpace(options.CTrader.RedirectUri))
        {
            return Result<CTraderTokenExchangeResult>.Failure("CTrader:RedirectUri is required.");
        }

        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = options.CTrader.RedirectUri,
            ["client_id"] = options.CTrader.ClientId,
            ["client_secret"] = options.CTrader.ClientSecret
        };

        var tokenResponse = await RequestTokenAsync(parameters, cancellationToken);
        if (!tokenResponse.IsSuccess)
        {
            return Result<CTraderTokenExchangeResult>.Failure(tokenResponse.Error!);
        }

        return Result<CTraderTokenExchangeResult>.Success(new CTraderTokenExchangeResult(
            tokenResponse.Value!.AccessToken!,
            tokenResponse.Value.RefreshToken!,
            tokenResponse.Value.ExpiresIn));
    }

    private async Task<Result<CTraderTokenResponse>> RequestTokenAsync(Dictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync($"{options.CTrader.TokenEndpoint}?{BuildQueryString(parameters)}", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return Result<CTraderTokenResponse>.Failure($"Unable to connect to cTrader token endpoint: {exception.Message}");
        }

        using (response)
        {
            var tokenResponse = await response.Content.ReadFromJsonAsync<CTraderTokenResponse>(cancellationToken);

            if (!response.IsSuccessStatusCode || tokenResponse is null)
            {
                var details = tokenResponse?.Description ?? tokenResponse?.ErrorDescription ?? tokenResponse?.ErrorCode ?? tokenResponse?.Error ?? response.ReasonPhrase ?? "Unknown cTrader token error.";
                return Result<CTraderTokenResponse>.Failure($"cTrader token endpoint failed: {details}");
            }

            if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                var details = tokenResponse.Description ?? tokenResponse.ErrorDescription ?? tokenResponse.ErrorCode ?? tokenResponse.Error ?? "cTrader response did not include an access token.";
                return Result<CTraderTokenResponse>.Failure($"cTrader token endpoint did not return an access token: {details}");
            }

            if (string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
            {
                return Result<CTraderTokenResponse>.Failure("cTrader token endpoint did not return a refresh token.");
            }

            return Result<CTraderTokenResponse>.Success(tokenResponse);
        }
    }

    private Result ValidateRequiredSettings()
    {
        if (string.IsNullOrWhiteSpace(options.CTrader.ClientId))
        {
            return Result.Failure("CTrader:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.CTrader.ClientSecret))
        {
            return Result.Failure("CTrader:ClientSecret is required.");
        }

        return Result.Success();
    }

    private static string BuildQueryString(Dictionary<string, string?> parameters) =>
        string.Join("&", parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
}

public sealed record CTraderTokenTestResult(
    bool IsConnected,
    string Message,
    bool HasAccessToken,
    bool HasRefreshToken,
    long? ExpiresInSeconds,
    string? AccessToken,
    string? RefreshToken);

public sealed record CTraderTokenExchangeResult(
    string AccessToken,
    string RefreshToken,
    long? ExpiresInSeconds);

internal sealed class CTraderTokenResponse
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiresIn")]
    public long? ExpiresIn { get; set; }

    [JsonPropertyName("tokenType")]
    public string? TokenType { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

using System.Security.Cryptography;
using System.Text;
using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CoinTracker.Modules.Users;

public sealed record FavoriteCoinInput(string CoinCode, decimal ThresholdPercent);
public sealed record UserCreateRequest(string Name, string Email, List<FavoriteCoinInput>? FavoriteCoins, List<string>? DiscordWebhookUrls);
public sealed record DiscordEndpointInput(Guid? EndpointId, string? Url);
public sealed record UserUpdateRequest(string Name, string Email, List<FavoriteCoinInput>? FavoriteCoins, List<DiscordEndpointInput>? DiscordEndpoints);
public sealed record EmailLookupRequest(string Email);
public sealed record FavoriteCoinResponse(string CoinCode, decimal ThresholdPercent);
public sealed record DiscordEndpointResponse(Guid Id, string Identifier);
public sealed record UserProfileResponse(Guid UserId, string Name, string Email, IReadOnlyList<FavoriteCoinResponse> FavoriteCoins, IReadOnlyList<DiscordEndpointResponse> DiscordEndpoints);

public sealed class UserProfileService
{
    private readonly IMongoCollection<UserDocument> _users;
    private readonly byte[] _key;

    public UserProfileService(MongoStore store, IOptions<UserSecretsOptions> options)
    {
        _users = store.Database.GetCollection<UserDocument>(MongoStore.UsersCollection);
        try { _key = Convert.FromBase64String(options.Value.EncryptionKey); }
        catch (FormatException) { throw new InvalidOperationException("UserSecrets:EncryptionKey must be a base64-encoded 32-byte key."); }
        if (_key.Length != 32) throw new InvalidOperationException("UserSecrets:EncryptionKey must be a base64-encoded 32-byte key.");
    }

    public async Task<UserProfileResponse> CreateAsync(UserCreateRequest request, CancellationToken token)
    {
        var user = Build(request.Name, request.Email, request.FavoriteCoins, request.DiscordWebhookUrls);
        user.Id = Guid.NewGuid();
        user.CreatedAtUtc = user.UpdatedAtUtc = DateTime.UtcNow;
        try { await _users.InsertOneAsync(user, cancellationToken: token); }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey) { throw new UserConflictException("Email is already registered."); }
        return ToResponse(user);
    }

    public async Task<UserProfileResponse?> GetAsync(Guid id, CancellationToken token) =>
        ToResponseOrNull(await _users.Find(x => x.Id == id).FirstOrDefaultAsync(token));

    public async Task<Guid?> LookupAsync(string email, CancellationToken token)
    {
        var normalized = NormalizeEmail(email);
        var user = await _users.Find(x => x.NormalizedEmail == normalized).Project(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
        return user;
    }

    public async Task<UserProfileResponse?> UpdateAsync(Guid id, UserUpdateRequest request, CancellationToken token)
    {
        var replacement = Build(request.Name, request.Email, request.FavoriteCoins, []);
        var existing = await _users.Find(x => x.Id == id).FirstOrDefaultAsync(token);
        if (existing is null) return null;
        replacement.Id = id;
        replacement.CreatedAtUtc = existing.CreatedAtUtc;
        replacement.UpdatedAtUtc = DateTime.UtcNow;
        var inputs = request.DiscordEndpoints ?? [];
        if (inputs.Count > 5) throw new UserValidationException("At most five Discord webhook endpoints may be registered.");
        var retained = new List<DiscordEndpointDocument>();
        foreach (var input in inputs)
        {
            if (input.EndpointId is { } endpointId)
            {
                var previous = existing.DiscordEndpoints.FirstOrDefault(x => x.Id == endpointId);
                if (previous is null) throw new UserValidationException("A supplied Discord endpoint ID does not belong to this profile.");
                if (string.IsNullOrWhiteSpace(input.Url)) { retained.Add(previous); continue; }
                var replacementUrl = ParseDiscordUrl(input.Url);
                retained.Add(new DiscordEndpointDocument { Id = endpointId, EncryptedUrl = Encrypt(replacementUrl) });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(input.Url)) throw new UserValidationException("A URL is required for a new Discord endpoint.");
                retained.Add(new DiscordEndpointDocument { Id = Guid.NewGuid(), EncryptedUrl = Encrypt(ParseDiscordUrl(input.Url)) });
            }
        }
        if (retained.Select(x => Decrypt(x.EncryptedUrl)).Distinct(StringComparer.Ordinal).Count() != retained.Count)
            throw new UserValidationException("Discord webhook endpoints must be unique.");
        replacement.DiscordEndpoints = retained;
        try
        {
            var result = await _users.FindOneAndReplaceAsync(x => x.Id == id,
                replacement,
                new FindOneAndReplaceOptions<UserDocument> { ReturnDocument = ReturnDocument.After }, token);
            return ToResponseOrNull(result);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey) { throw new UserConflictException("Email is already registered."); }
    }

    private UserDocument Build(string name, string email, List<FavoriteCoinInput>? favorites, List<string>? endpoints)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150) throw new UserValidationException("Name is required and must be at most 150 characters.");
        var normalizedEmail = NormalizeEmail(email);
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase))
            throw new UserValidationException("A valid email address is required.");
        favorites ??= [];
        endpoints ??= [];
        if (favorites.Any(x => string.IsNullOrWhiteSpace(x.CoinCode) || x.ThresholdPercent <= 0)) throw new UserValidationException("Favorite coin codes and positive thresholds are required.");
        var normalizedFavorites = favorites.Select(x => new FavoriteCoinDocument { CoinCode = x.CoinCode.Trim().ToLowerInvariant(), ThresholdPercent = x.ThresholdPercent }).ToList();
        if (normalizedFavorites.Select(x => x.CoinCode).Distinct(StringComparer.Ordinal).Count() != normalizedFavorites.Count) throw new UserValidationException("Favorite coins must be unique.");
        var parsedEndpoints = NormalizeEndpoints(endpoints);
        return new UserDocument
        {
            Name = name.Trim(), Email = parsed.Address, NormalizedEmail = normalizedEmail,
            FavoriteCoins = normalizedFavorites,
            DiscordEndpoints = parsedEndpoints.Select(url => new DiscordEndpointDocument { Id = Guid.NewGuid(), EncryptedUrl = Encrypt(url) }).ToList()
        };
    }

    private static List<string> NormalizeEndpoints(List<string> endpoints)
    {
        if (endpoints.Count > 5) throw new UserValidationException("At most five Discord webhook endpoints may be registered.");
        var parsed = endpoints.Select(ParseDiscordUrl).ToList();
        if (parsed.Distinct(StringComparer.Ordinal).Count() != parsed.Count) throw new UserValidationException("Discord webhook endpoints must be unique.");
        return parsed;
    }

    private static string ParseDiscordUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "discord.com" || uri.Port != 443 ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, "^/api/webhooks/[0-9]+/[A-Za-z0-9._-]+$"))
            throw new UserValidationException("Only valid HTTPS Discord incoming webhook URLs are accepted.");
        return uri.AbsoluteUri;
    }

    private static string NormalizeEmail(string email) => (email ?? "").Trim().ToLowerInvariant();
    private string Encrypt(string text)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var plain = Encoding.UTF8.GetBytes(text); var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_key, 16); aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public string Decrypt(string encrypted)
    {
        var data = Convert.FromBase64String(encrypted); var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(_key, 16); aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
    private static UserProfileResponse? ToResponseOrNull(UserDocument? user) => user is null ? null : new UserProfileResponse(user.Id, user.Name, user.Email,
        user.FavoriteCoins.Select(x => new FavoriteCoinResponse(x.CoinCode, x.ThresholdPercent)).ToList(),
        user.DiscordEndpoints.Select(x => new DiscordEndpointResponse(x.Id, x.MaskedIdentifier)).ToList());
    private static UserProfileResponse ToResponse(UserDocument user) => ToResponseOrNull(user)!;
}

public sealed class UserValidationException(string message) : Exception(message);
public sealed class UserConflictException(string message) : Exception(message);

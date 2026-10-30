using MuzLoto.Web.Domain;
using QRCoder;
using TL;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// Sends from a Telegram account linked by QR code (MTProto via WTelegramClient), like linking Telegram Desktop:
/// on «Мессенджеры» the admin shows a QR code, the person whose account will send scans it in Telegram
/// (Настройки → Устройства → Подключить устройство). The session file lives in Messaging:DataPath on the server
/// volume; the site's Telegram application keys are Telegram:ApiId / Telegram:ApiHash in .env.
/// </summary>
public sealed class TelegramChannel : IMessengerChannel, IAsyncDisposable
{
    private static readonly TimeSpan QrLifetime = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> NotForQrLogin =
        ["phone_number", "verification_code", "first_name", "last_name", "email", "email_verification_code"];

    private readonly IConfiguration config;
    private readonly ILogger<TelegramChannel> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WTelegram.Client? client;
    private bool resumeTried;
    private CancellationTokenSource? qrLogin;
    private string? twoFactorPassword;

    public TelegramChannel(IConfiguration config, ILogger<TelegramChannel> logger, IWebHostEnvironment env)
    {
        this.config = config;
        this.logger = logger;
        DataPath = config["Messaging:DataPath"] is { Length: > 0 } path ? path : Path.Combine(env.ContentRootPath, "App_Data", "messengers");
        WTelegram.Helpers.Log = (level, text) => { if (level >= 3) logger.Log((LogLevel)level, "Telegram: {Text}", text); };
    }

    public MessageChannel Channel => MessageChannel.Telegram;
    private string DataPath { get; }
    private string SessionPath => Path.Combine(DataPath, "telegram.session");
    private int ApiId => int.TryParse(config["Telegram:ApiId"], out var id) ? id : 0;
    private string? ApiHash => config["Telegram:ApiHash"];
    public bool IsConfigured => ApiId > 0 && !string.IsNullOrWhiteSpace(ApiHash);

    /// <summary>The tg://login link to show as a QR code while waiting for the scan; null otherwise.</summary>
    public string? QrUrl { get; private set; }
    public bool WaitingForScan => qrLogin is { IsCancellationRequested: false };
    /// <summary>Why the last QR sign-in did not finish, in words for the admin.</summary>
    public string? LoginError { get; private set; }
    public string? AccountName => client?.User is { } u
        ? string.Join(" ", new[] { u.first_name, u.last_name }.Where(x => !string.IsNullOrWhiteSpace(x))) + (u.username is { Length: > 0 } n ? $" (@{n})" : "")
        : null;

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        if (!IsConfigured) return false;
        await gate.WaitAsync(ct);
        try
        {
            // After a restart the saved session signs in by itself.
            if (client?.User is null && !resumeTried && !WaitingForScan && File.Exists(SessionPath))
            {
                resumeTried = true;
                try { await Client().LoginUserIfNeeded(); }
                catch (Exception ex) { logger.LogWarning(ex, "Telegram session could not be resumed; link the account again"); }
            }
            return client?.User is not null && !WaitingForScan;
        }
        finally { gate.Release(); }
    }

    /// <summary>Starts waiting for a QR scan (for 5 minutes). The 2FA password, if the account has one, is needed up front.</summary>
    public async Task StartQrLoginAsync(string? password, CancellationToken ct)
    {
        if (!IsConfigured) throw new BusinessException("Telegram не настроен: на сервере нет TELEGRAM_API_ID и TELEGRAM_API_HASH.");
        await gate.WaitAsync(ct);
        try
        {
            qrLogin?.Cancel();
            if (client is not null) await client.DisposeAsync();
            client = null;
            if (File.Exists(SessionPath)) File.Delete(SessionPath);
            twoFactorPassword = string.IsNullOrWhiteSpace(password) ? null : password;
            LoginError = null;
            QrUrl = null;
            var cts = qrLogin = new CancellationTokenSource(QrLifetime);
            var telegram = Client();
            _ = Task.Run(async () =>
            {
                try
                {
                    var user = await telegram.LoginWithQRCode(url => QrUrl = url, logoutFirst: false, ct: cts.Token);
                    logger.LogInformation("Telegram linked by QR code as user {UserId}", user.id);
                }
                catch (OperationCanceledException) { LoginError = "QR-код устарел: нажмите «Показать QR-код» ещё раз."; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Telegram QR sign-in failed");
                    LoginError = ex.Message.Contains("PASSWORD") || ex is WrongPasswordNeeded
                        ? "На аккаунте включена двухэтапная проверка: введите её пароль и покажите QR-код заново."
                        : "Telegram не завершил вход: " + ex.Message;
                }
                finally
                {
                    QrUrl = null;
                    twoFactorPassword = null;
                    cts.Cancel();
                }
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public byte[]? QrPng() => QrUrl is { } url ? new PngByteQRCode(QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M)).GetGraphic(8) : null;

    public async Task LogoutAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            qrLogin?.Cancel();
            if (client?.User is not null) { try { await client.Auth_LogOut(); } catch (Exception ex) { logger.LogWarning(ex, "Telegram logout"); } }
            if (client is not null) await client.DisposeAsync();
            client = null;
            QrUrl = null;
            if (File.Exists(SessionPath)) File.Delete(SessionPath);
        }
        finally { gate.Release(); }
    }

    public async Task<SendOutcome> SendAsync(Booking booking, string text, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (client?.User is null || WaitingForScan) return SendOutcome.Failed("Telegram не подключён.");
            InputPeer? peer = null;
            // The nickname from the form first; without it (or if it does not exist) — the phone number.
            if (booking.Telegram is { Length: > 1 } nick)
            {
                try { peer = await client.Contacts_ResolveUsername(nick.TrimStart('@')); }
                catch (RpcException ex) when (ex.Message is "USERNAME_NOT_OCCUPIED" or "USERNAME_INVALID") { }
            }
            if (peer is null)
            {
                var imported = await client.Contacts_ImportContacts([new InputPhoneContact
                {
                    client_id = Random.Shared.NextInt64(), phone = booking.Phone, first_name = booking.CustomerName, last_name = booking.TeamName
                }]);
                if (imported.imported.Length == 0)
                    return SendOutcome.Skipped("Клиент не найден в Telegram: нет ника, а номер не зарегистрирован или скрыт настройками.");
                peer = imported.users[imported.imported[0].user_id];
            }
            await client.SendMessageAsync(peer, text);
            return SendOutcome.Sent;
        }
        catch (RpcException ex) when (ex.Message.StartsWith("FLOOD_WAIT") || ex.Message == "PEER_FLOOD")
        {
            return SendOutcome.Failed("Telegram временно ограничил отправку, повторим позже.", TimeSpan.FromSeconds(Math.Max(ex.X, 600)));
        }
        catch (RpcException ex) when (ex.Message.Contains("PRIVACY") || ex.Message is "USER_IS_BLOCKED" or "INPUT_USER_DEACTIVATED")
        {
            return SendOutcome.Skipped("Клиент запретил сообщения от незнакомых или удалил аккаунт.");
        }
        catch (RpcException ex) { return SendOutcome.Failed("Telegram: " + ex.Message); }
        finally { gate.Release(); }
    }

    private WTelegram.Client Client()
    {
        if (client is not null) return client;
        Directory.CreateDirectory(DataPath);
        return client = new WTelegram.Client(Config);
    }

    /// <summary>Answers the library's questions. Only QR sign-in is used: phone and SMS-code questions are refused.</summary>
    private string? Config(string what) => what switch
    {
        "api_id" => ApiId.ToString(),
        "api_hash" => ApiHash,
        "session_pathname" => SessionPath,
        "password" => twoFactorPassword ?? throw new WrongPasswordNeeded(),
        "device_model" => "МузЛото (сайт)",
        _ when NotForQrLogin.Contains(what) => throw new InvalidOperationException("Telegram: вход только по QR-коду на странице «Мессенджеры»."),
        _ => null
    };

    private sealed class WrongPasswordNeeded() : Exception("PASSWORD_NEEDED");

    public async ValueTask DisposeAsync()
    {
        qrLogin?.Cancel();
        if (client is not null) await client.DisposeAsync();
    }
}

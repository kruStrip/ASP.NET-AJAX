using MuzLoto.Web.Domain;
using TL;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// Sends from the company's personal Telegram account (MTProto via WTelegramClient). The admin signs in once on
/// «Мессенджеры» with the code Telegram sends to the account; the session file lives in Messaging:DataPath on the
/// server volume (never in the repository). Credentials: Telegram:ApiId / Telegram:ApiHash / Telegram:Phone in .env.
/// </summary>
public sealed class TelegramChannel : IMessengerChannel, IAsyncDisposable
{
    private readonly IConfiguration config;
    private readonly ILogger<TelegramChannel> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WTelegram.Client? client;
    private bool resumeTried;

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
    public string? Phone => ContactRules.NormalizePhone(config["Telegram:Phone"]);
    public bool IsConfigured => ApiId > 0 && !string.IsNullOrWhiteSpace(ApiHash) && Phone is not null;

    /// <summary>What the login form asks next: "verification_code", "password", or null when signed in.</summary>
    public string? NextLoginStep { get; private set; }
    public string? AccountName => client?.User is { } u ? (u.username is { Length: > 0 } name ? "@" + name : u.first_name) : null;

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        if (!IsConfigured) return false;
        await gate.WaitAsync(ct);
        try
        {
            // After a restart the saved session signs in by itself; without a session nothing is sent to Telegram.
            if (client?.User is null && !resumeTried && File.Exists(SessionPath))
            {
                resumeTried = true;
                try { NextLoginStep = await Client().Login(Phone!); }
                catch (Exception ex) { logger.LogWarning(ex, "Telegram session could not be resumed"); }
            }
            return client?.User is not null;
        }
        finally { gate.Release(); }
    }

    /// <summary>Starts or continues the sign-in: the phone first, then the code, then the 2FA password if set.</summary>
    public async Task<string?> LoginAsync(string? value, CancellationToken ct)
    {
        if (!IsConfigured) throw new BusinessException("Telegram не настроен: на сервере нет Telegram__ApiId, Telegram__ApiHash и Telegram__Phone.");
        await gate.WaitAsync(ct);
        try
        {
            NextLoginStep = await Client().Login(NextLoginStep is null ? Phone! : value?.Trim() ?? "");
            return NextLoginStep;
        }
        catch (RpcException ex)
        {
            throw new BusinessException(ex.Message switch
            {
                "PHONE_CODE_INVALID" => "Неверный код из Telegram.",
                "PHONE_CODE_EXPIRED" => "Код устарел. Нажмите «Получить код» ещё раз.",
                "PASSWORD_HASH_INVALID" => "Неверный пароль двухэтапной проверки.",
                _ => "Telegram отклонил вход: " + ex.Message
            });
        }
        finally { gate.Release(); }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (client?.User is not null) { try { await client.Auth_LogOut(); } catch (Exception ex) { logger.LogWarning(ex, "Telegram logout"); } }
            if (client is not null) await client.DisposeAsync();
            client = null;
            NextLoginStep = null;
            if (File.Exists(SessionPath)) File.Delete(SessionPath);
        }
        finally { gate.Release(); }
    }

    public async Task<SendOutcome> SendAsync(Booking booking, string text, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (client?.User is null) return SendOutcome.Failed("Telegram не подключён.");
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
        return client = new WTelegram.Client(ApiId, ApiHash!, SessionPath);
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync();
    }
}

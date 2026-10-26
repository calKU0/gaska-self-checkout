using cdn_api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp.XlApi;

/// <summary>
/// Single XL API session shared by all XL services (documents, customers, prices). XL API keeps one session per
/// process – a second XLLogin returns the session already open. Logs in lazily and re-logs in when the session expires.
/// </summary>
public sealed class XlSession : IDisposable
{
    // XLOpisBledu function numbers
    public const int FnNewDocument = 1, FnAddLine = 2, FnAddPayment = 6, FnCloseDocument = 7, FnNewCustomer = 12, FnCloseCustomer = 13;

    private readonly IOptionsMonitor<ErpOptions> _options;
    private readonly ILogger<XlSession> _logger;
    private readonly Lazy<XlApiThread> _thread;
    private int _sessionId;
    private int _disposed;
    // a failed login is not repeated for every queued call – repeated failed logins crash XL API (access violation)
    private InvalidOperationException? _loginFailure;
    private DateTime _loginRetryAt;

    public XlSession(IOptionsMonitor<ErpOptions> options, ILogger<XlSession> logger)
    {
        _options = options;
        _logger = logger;
        _thread = new Lazy<XlApiThread>(() => new XlApiThread());
    }

    public XlApiOptions Settings => _options.CurrentValue.XlApi;
    public ErpOptions Erp => _options.CurrentValue;
    public int SessionId => _sessionId;
    public int ApiVersion => Settings.ApiVersion;

    /// <summary>Runs <paramref name="func"/> on the XL thread with an active session.</summary>
    public Task<T> RunAsync<T>(Func<int, T> func, bool highPriority = true) =>
        _thread.Value.InvokeAsync(() =>
        {
            EnsureLogin();
            return func(_sessionId);
        }, highPriority);

    /// <summary>Logs in again after the session expired (XL errors 61 / 236 / 339). Must be called on the XL thread.</summary>
    public void Relogin()
    {
        _sessionId = 0;
        EnsureLogin();
    }

    /// <summary>Session expired / not active ("Sesja nie jest aktywna" – e.g. XL logged the session out after inactivity).</summary>
    public static bool IsSessionError(int code) => code is 61 or 236 or 339;

    /// <summary>Runs an XL API call (on the XL thread) and repeats it once after logging in again when the session expired.</summary>
    public int CallWithRelogin(Func<int> call)
    {
        var rc = call();
        if (!IsSessionError(rc))
            return rc;
        _logger.LogWarning("XL session expired (code {Code}) – logging in again", rc);
        Relogin();
        return call();
    }

    public string Describe(int function, int code)
    {
        try
        {
            var info = new XLKomunikatInfo_20251 { Wersja = ApiVersion, Funkcja = function, Blad = code, Tryb = 0 };
            return cdn_api.cdn_api.XLOpisBledu(info) == 0 && !string.IsNullOrWhiteSpace(info.OpisBledu) ? info.OpisBledu.Trim() : "no description";
        }
        catch
        {
            return "no description";
        }
    }

    private void EnsureLogin()
    {
        if (_sessionId != 0)
            return;
        if (_loginFailure is not null && DateTime.UtcNow < _loginRetryAt)
            throw _loginFailure;

        var s = Settings;
        var login = new XLLoginInfo_20251
        {
            Wersja = s.ApiVersion,
            ProgramID = s.ProgramId,
            Baza = s.Database,
            OpeIdent = s.Operator,
            OpeHaslo = s.Password,
            TrybWsadowy = s.LoginBatchMode,
            TrybNaprawy = 1,
            UtworzWlasnaSesje = 1,
            Winieta = -1,
            PlikLog = s.LogFile ?? "",
            Serwer = s.Server ?? "",
            KluczAPI = s.ApiKey ?? ""
        };

        // logged before the native call – a crash inside XL API (e.g. under a debugger) is then easy to locate
        _logger.LogInformation("Logging in to XL API: database {Database}, operator {Operator}, debugger attached: {Debugger}",
            s.Database, s.Operator, System.Diagnostics.Debugger.IsAttached);
        var session = 0;
        var rc = cdn_api.cdn_api.XLLogin(login, ref session);
        if (rc != 0 || session == 0)
        {
            _loginFailure = new InvalidOperationException($"XL login failed (code {rc}: {LoginError(rc)}).");
            _loginRetryAt = DateTime.UtcNow.AddMinutes(1);
            throw _loginFailure;
        }

        _loginFailure = null;
        _sessionId = session;
        _logger.LogInformation("Logged in to XL API, session {Session}", session);
    }

    private static string LoginError(int rc) => rc switch
    {
        -9 => "operator is not assigned to any company unit",
        -8 => "database name missing",
        -7 => "database not registered",
        -6 => "password or operator missing",
        -5 => "invalid password",
        -4 => "operator account locked",
        -3 => "program name missing",
        -2 => "log file error",
        -1 => "invalid API version",
        1 => "initialization failed",
        2 => "program instance already running",
        3 => "operator may not log in multiple times",
        61 => "session could not be created",
        _ => "unknown error"
    };

    public void Dispose()
    {
        if (!_thread.IsValueCreated || Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            if (_sessionId != 0)
                _thread.Value.InvokeAsync(() => cdn_api.cdn_api.XLLogout(_sessionId)).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XL logout failed");
        }

        _thread.Value.Dispose();
    }
}

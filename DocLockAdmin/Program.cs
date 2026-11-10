using System.Collections.Concurrent;
using System.Security;
using System.Security.Principal;
using Microsoft.Data.SqlClient;
using Microsoft.Win32.SafeHandles;

// Document lock admin: view a user's documents, unlock a user's documents, view/unlock all locked documents
// for one client database. Ported from CMAdminTool (Form1.cs). Listens on localhost only by default.

// --reset-credentials: forget the saved passwords and ask again (removed before config parsing sees it)
bool resetCredentials = args.Contains("--reset-credentials", StringComparer.OrdinalIgnoreCase);
args = args.Where(a => !a.Equals("--reset-credentials", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(args);
// Local-only settings (passwords). Excluded from git; delete once passwords move to a secrets store.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
var opt = builder.Configuration.GetSection("DocLock").Get<DocLockOptions>() ?? new DocLockOptions();
builder.WebHost.UseUrls(opt.Url);

// Relative paths in appsettings.json are relative to the app folder (where appsettings.json lives)
var root = builder.Environment.ContentRootPath;
opt.AuditLog = Path.GetFullPath(opt.AuditLog, root);
var servers = ServerList.Load(Path.GetFullPath(opt.ServersCsv, root), opt.ServersCsv);
var sql = new Sql(opt, servers);
sql.LoadAccounts(resetCredentials);
sql.PromptForSqlLogins();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/environments", () =>
    servers.Environments.Select(e => new { name = e, auth = sql.AuthFor(e), servers = servers.For(e).Count }));

app.MapGet("/api/databases", async (string env, bool? refresh) =>
    await Run(async () => Results.Ok(await sql.DiscoverAsync(env, refresh == true))));

app.MapGet("/api/users", async (string env, string server, string db) =>
    await Run(async () => Results.Ok(await sql.QueryAsync(await sql.TargetAsync(env, server, db), Queries.Users))));

app.MapGet("/api/user-docs", async (string env, string server, string db, string user) =>
    await Run(async () => Results.Ok(await sql.QueryAsync(await sql.TargetAsync(env, server, db), Queries.UserDocs, ("@user", user)))));

app.MapGet("/api/locked", async (string env, string server, string db) =>
    await Run(async () => Results.Ok(await sql.QueryAsync(await sql.TargetAsync(env, server, db), Queries.LockedDocs))));

app.MapPost("/api/unlock-user", async (UnlockRequest r) => await Run(async () =>
{
    if (string.IsNullOrWhiteSpace(r.User)) return Results.BadRequest(new { error = "No user selected." });
    var t = await sql.TargetAsync(r.Env, r.Server, r.Db);
    int rows = await sql.ExecuteAsync(t, Queries.UnlockUserDocs, ("@user", r.User));
    sql.Audit(t, "UNLOCK USER DOCS", r.User, rows);
    return Results.Ok(new { rows });
}));

app.MapPost("/api/unlock-all", async (UnlockRequest r) => await Run(async () =>
{
    // The page asks the user to type the database name; the server checks it too
    if (!string.Equals(r.Confirm, r.Db, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Confirmation did not match the database name. Nothing was changed." });
    var t = await sql.TargetAsync(r.Env, r.Server, r.Db);
    int rows = await sql.ExecuteAsync(t, Queries.UnlockAllDocs);
    sql.Audit(t, "UNLOCK ALL DOCS", "*", rows);
    return Results.Ok(new { rows });
}));

// ---------- User logins (port of Remove-AppLogins-Interactive.ps1): find NAME_% on every server, disable ----------

app.MapGet("/api/logins", async (string env, string name) => await Run(async () =>
{
    var prefix = Logins.CleanPrefix(name, opt.MinLoginPrefixLength);
    return Results.Ok(await sql.FindLoginsAsync(env, prefix));
}));

app.MapPost("/api/disable-logins", async (DisableRequest r) => await Run(async () =>
{
    var prefix = Logins.CleanPrefix(r.Name, opt.MinLoginPrefixLength);
    if (!string.Equals(r.Confirm?.Trim(), prefix, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Confirmation did not match the name. Nothing was changed." });
    if (r.Logins is null || r.Logins.Count == 0) return Results.BadRequest(new { error = "No logins selected." });

    var results = new List<object>();
    foreach (var l in r.Logins)
    {
        // Only logins that belong to the searched name can be disabled through this call
        if (!l.Login.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new { l.Server, l.Login, rows = 0, error = $"'{l.Login}' does not start with {prefix}_ (skipped)" });
            continue;
        }
        try
        {
            var t = sql.AuthTarget(r.Env, l.Server);
            int rows = await sql.ExecuteAsync(t, Queries.DisableLogin, ("@login", l.Login));
            sql.Audit(t, "DISABLE LOGIN", l.Login, rows);
            results.Add(new { server = t.Server, l.Login, rows, error = (string?)null });
        }
        catch (Exception ex) when (ex is SqlException or BadTargetException)
        {
            results.Add(new { l.Server, l.Login, rows = 0, error = ex.Message });
        }
    }
    return Results.Ok(results);
}));

// ---------- Application users (AL.UserIndex in each client database): enable/disable + move folder ----------

app.MapGet("/api/app-users", async (string env, string name) => await Run(async () =>
    Results.Ok(await sql.FindAppUsersAsync(env, Logins.CleanPrefix(name, opt.MinLoginPrefixLength)))));

app.MapPost("/api/app-users/set-enabled", async (AppUserRequest r) => await Run(async () =>
{
    var prefix = Logins.CleanPrefix(r.Name, opt.MinLoginPrefixLength);
    if (!string.Equals(r.Confirm?.Trim(), prefix, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Confirmation did not match the name. Nothing was changed." });
    if (r.Users is null || r.Users.Count == 0) return Results.BadRequest(new { error = "No users selected." });

    var folder = r.Enabled ? opt.EnabledUsersFolder : opt.DisabledUsersFolder;
    var action = r.Enabled ? "ENABLE APP USER" : "DISABLE APP USER";
    var results = new List<object>();
    foreach (var u in r.Users)
    {
        if (!u.User.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new { u.Server, u.Db, u.User, rows = 0, error = $"'{u.User}' does not start with {prefix}_ (skipped)" });
            continue;
        }
        try
        {
            var t = await sql.TargetAsync(r.Env, u.Server, u.Db);
            var res = await sql.QueryAsync(t, Queries.SetAppUserEnabled, ("@user", u.User), ("@enabled", r.Enabled), ("@folder", folder));
            int rows = Convert.ToInt32(res.Rows.FirstOrDefault()?["n"] ?? 0);
            sql.Audit(t, action, $"{u.User} -> {folder}", rows);
            results.Add(new { server = t.Server, db = t.Db, u.User, rows, error = (string?)null });
        }
        catch (Exception ex) when (ex is SqlException or BadTargetException)
        {
            results.Add(new { u.Server, u.Db, u.User, rows = 0, error = ex.Message });
        }
    }
    return Results.Ok(results);
}));

Console.WriteLine($"\nDocLockAdmin running at {opt.Url}  (Ctrl+C to stop)\n");
app.Run();

// Turns expected failures into a JSON error the page can show, instead of a 500 page
static async Task<IResult> Run(Func<Task<IResult>> action)
{
    try { return await action(); }
    catch (BadTargetException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (SqlException ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
}

public class DocLockOptions
{
    public string Url { get; set; } = "http://localhost:5080";
    public string ServersCsv { get; set; } = @"..\Servers.csv";
    public string[] ExcludeDatabases { get; set; } = [];
    public bool TrustServerCertificate { get; set; } = true;
    public int QueryTimeoutSeconds { get; set; } = 60;
    public int CacheMinutes { get; set; } = 30;
    public string AuditLog { get; set; } = "unlock_audit.csv";
    // Per environment: "Windows" (default) or "Sql" (prompts for a SQL login at startup, kept in memory only)
    public Dictionary<string, string> EnvironmentAuth { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Per environment: Windows account (DOMAIN\user) to connect as. Password comes from Windows Credential Manager,
    // generic credential "DocLockAdmin:<Environment>". Environments not listed use the account running the site.
    public Dictionary<string, string> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // TEMPORARY, local use only: plain-text passwords per environment (put them in appsettings.Local.json).
    // Used before Credential Manager. To be replaced by a secrets store (e.g. AWS Secrets Manager).
    public Dictionary<string, string> Passwords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string AuthDatabase { get; set; } = "AlterianAuth";
    public int MinLoginPrefixLength { get; set; } = 3;
    public string EnabledUsersFolder { get; set; } = "Users";
    public string DisabledUsersFolder { get; set; } = "Disabled Users";
}

public record LoginRef(string Server, string Login);
public record AppUserRef(string Server, string Db, string User);
public record AppUserRequest(string Env, string Name, string? Confirm, bool Enabled, List<AppUserRef>? Users);
public record DisableRequest(string Env, string Name, string? Confirm, List<LoginRef>? Logins);

public static class Logins
{
    // "ABISHEKK", "ABISHEKK_" and "ABISHEKK_%" all mean the same prefix
    public static string CleanPrefix(string? name, int minLength)
    {
        var p = (name ?? "").Trim().TrimEnd('_', '%');
        if (p.Length < minLength) throw new BadTargetException($"Name must be at least {minLength} characters.");
        return p;
    }

    // LIKE pattern for "<prefix>_%" with the prefix taken literally (used with ESCAPE '\')
    public static string LikePattern(string prefix) =>
        System.Text.RegularExpressions.Regex.Replace(prefix, @"([\\%_\[])", @"\$1") + @"\_%";
}

public record UnlockRequest(string Env, string Server, string Db, string? User, string? Confirm);
public record Target(string Env, string Server, string Db);
public class BadTargetException(string message) : Exception(message);

public static class Queries
{
    // System databases skipped; only databases that actually have the document tables are clients
    public const string ClientDatabases = """
        SELECT name FROM sys.databases
        WHERE database_id > 4 AND state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1
          AND OBJECT_ID(QUOTENAME(name) + '.AL.DocumentIndex') IS NOT NULL
        ORDER BY name
        """;

    public const string Users = "SELECT name, enabled FROM AL.UserIndex ORDER BY name";

    // Documents authored by the user (CMAdminTool "View Docs"), plus who currently holds the lock
    public const string UserDocs = """
        SELECT y.[name] AS Author, x.ID AS Id, x.[Name] AS DocumentName, z.[name] AS FolderName,
               x.[Type], x.[CreateDate], x.[IsLocked], l.[name] AS LockedBy
        FROM AL.DocumentIndex x
        INNER JOIN AL.UserIndex y ON y.id = x.AuthorID
        INNER JOIN AL.FolderIndex z ON z.id = x.FolderID
        LEFT JOIN AL.UserIndex l ON l.id = x.LockingUserID
        WHERE y.[name] = @user
        ORDER BY x.[Name]
        """;

    // Same effect as CMAdminTool "Unlock Docs": clears locks held by this user
    public const string UnlockUserDocs = """
        UPDATE AL.DocumentIndex SET LockingUserID = NULL
        WHERE LockingUserID IN (SELECT id FROM AL.UserIndex WHERE [name] = @user)
        """;

    public const string LockedDocs = """
        SELECT x.ID AS DocID, x.[Name] AS DocName, a.[name] AS AuthorName, l.[name] AS LockingUserName,
               x.LockingUserID, x.LastEditorID, x.FolderID, f.[name] AS FolderName
        FROM AL.DocumentIndex x
        INNER JOIN AL.UserIndex a ON a.id = x.AuthorID
        INNER JOIN AL.UserIndex l ON l.id = x.LockingUserID
        LEFT JOIN AL.FolderIndex f ON f.id = x.FolderID
        WHERE x.LockingUserID IS NOT NULL AND x.IsLocked = 1
        ORDER BY a.[name], x.[Name]
        """;

    // Runs in the AlterianAuth database (DocLock:AuthDatabase)
    public const string FindLogins = """
        SELECT * FROM AUTH.LoginIndex WHERE [login] LIKE @pattern ESCAPE '\' ORDER BY [login]
        """;

    // Application users in a client database, with the name of the folder they sit in
    public const string FindAppUsers = """
        SELECT u.id, u.[name], u.enabled, f.[name] AS folder, u.emailaddress, u.title, u.created, u.modified
        FROM AL.UserIndex u
        LEFT JOIN AL.FolderIndex f ON f.id = u.folderID
        WHERE u.[name] LIKE @pattern ESCAPE '\'
        ORDER BY u.[name]
        """;

    // Enable/disable an application user and move it to the matching user folder (folderType 1).
    // Refuses to change anything unless exactly one folder with that name exists.
    public const string SetAppUserEnabled = """
        SET NOCOUNT ON;
        IF (SELECT COUNT(*) FROM AL.FolderIndex WHERE folderType = 1 AND [name] = @folder) <> 1
            THROW 50001, 'Expected exactly one user folder (folderType 1) with the target name in this database. Nothing was changed.', 1;
        UPDATE AL.UserIndex
           SET enabled = @enabled,
               folderID = (SELECT id FROM AL.FolderIndex WHERE folderType = 1 AND [name] = @folder)
         WHERE [name] = @user;
        SELECT @@ROWCOUNT AS n;
        """;

    // Exact login only, and only if still enabled, so re-running is harmless
    public const string DisableLogin = "UPDATE AUTH.LoginIndex SET enabled = 0 WHERE [login] = @login AND enabled <> 0";

    // Same effect as CMAdminTool "Unlock All"
    public const string UnlockAllDocs = "UPDATE AL.DocumentIndex SET LockingUserID = NULL WHERE LockingUserID IS NOT NULL";
}

// Servers.csv: columns Server (or Servers) and Environment, same file the PowerShell scripts use
public class ServerList(Dictionary<string, List<string>> byEnv)
{
    public IEnumerable<string> Environments => byEnv.Keys.OrderBy(k => k);
    public List<string> For(string env) => byEnv.TryGetValue(env, out var s) ? s : [];

    public static ServerList Load(string path, string configured)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Servers file not found: {path} (DocLock:ServersCsv = {configured})");
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var header = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToList();
        int si = header.FindIndex(h => h.Equals("Server", StringComparison.OrdinalIgnoreCase) || h.Equals("Servers", StringComparison.OrdinalIgnoreCase));
        int ei = header.FindIndex(h => h.Equals("Environment", StringComparison.OrdinalIgnoreCase));
        if (si < 0 || ei < 0) throw new InvalidDataException($"{path} needs a Server (or Servers) and an Environment column.");

        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cols in lines.Skip(1).Select(l => l.Split(',').Select(c => c.Trim().Trim('"')).ToArray()))
        {
            if (cols.Length <= Math.Max(si, ei) || cols[si] == "" || cols[ei] == "") continue;
            if (!map.TryGetValue(cols[ei], out var list)) map[cols[ei]] = list = [];
            if (!list.Contains(cols[si], StringComparer.OrdinalIgnoreCase)) list.Add(cols[si]);
        }
        return new ServerList(map);
    }
}

public class Sql(DocLockOptions opt, ServerList servers)
{
    private readonly Dictionary<string, SqlCredential> _creds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, List<ServerDatabases> Result)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _auditLock = new();

    public record ServerDatabases(string Server, List<string> Databases, string? Error);

    private readonly Dictionary<string, SafeAccessTokenHandle> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _accountErrors = new(StringComparer.OrdinalIgnoreCase);

    public static string CredentialTarget(string env) => $"DocLockAdmin:{env}";
    private static string CurrentUser => $"{Environment.UserDomainName}\\{Environment.UserName}";

    public string AuthFor(string env) =>
        _accountErrors.TryGetValue(env, out var err) ? $"Not connected: {err}"
        : _accounts.TryGetValue(env, out var acct) ? $"Windows ({acct})"
        : _creds.TryGetValue(env, out var c) ? $"SQL ({c.UserId})"
        : $"Windows ({CurrentUser})";

    // One logon token per environment, from the password saved in Windows Credential Manager (asks once if missing)
    public void LoadAccounts(bool reset)
    {
        foreach (var env in servers.Environments)
        {
            if (!opt.Accounts.TryGetValue(env, out var account) || string.IsNullOrWhiteSpace(account)) continue;
            var target = CredentialTarget(env);
            try
            {
                if (reset) WindowsAccounts.Delete(target);
                var saved = opt.Passwords.TryGetValue(env, out var configured) && !string.IsNullOrEmpty(configured)
                    ? (account, configured)
                    : WindowsAccounts.Read(target);
                if (saved is null)
                {
                    if (Console.IsInputRedirected)
                    {
                        _accountErrors[env] = $"no saved password. Run: cmdkey /generic:{target} /user:{account} /pass";
                        Console.WriteLine($"{env}: {_accountErrors[env]}");
                        continue;
                    }
                    Console.Write($"Password for {account} ({env}), saved to Windows Credential Manager as {target}: ");
                    var pwd = ReadMaskedString();
                    WindowsAccounts.Write(target, account, pwd);
                    saved = (account, pwd);
                }
                // The account in Credential Manager wins, so it can be changed there without editing appsettings.json
                var user = string.IsNullOrWhiteSpace(saved.Value.User) ? account : saved.Value.User;
                _tokens[env] = WindowsAccounts.NetworkLogon(user, saved.Value.Password);
                _accounts[env] = user;
                Console.WriteLine($"{env}: connecting as {user}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
            {
                _accountErrors[env] = $"could not use {account}: {ex.Message}";
                Console.WriteLine($"{env}: {_accountErrors[env]}");
            }
        }
    }

    private static string ReadMaskedString()
    {
        var sb = new System.Text.StringBuilder();
        for (ConsoleKeyInfo k; (k = Console.ReadKey(true)).Key != ConsoleKey.Enter;)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } }
            else if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString();
    }

    // Runs the SQL work as the environment's account, if one is configured
    private Task<T> AsAccount<T>(string env, Func<Task<T>> work)
    {
        if (_accountErrors.TryGetValue(env, out var err)) throw new BadTargetException($"{env}: {err}");
        return _tokens.TryGetValue(env, out var token) ? WindowsIdentity.RunImpersonatedAsync(token, work) : work();
    }

    public void PromptForSqlLogins()
    {
        foreach (var env in servers.Environments)
        {
            if (!opt.EnvironmentAuth.TryGetValue(env, out var mode) || !mode.Equals("Sql", StringComparison.OrdinalIgnoreCase)) continue;
            Console.Write($"SQL login for {env}: ");
            var user = Console.ReadLine()?.Trim() ?? "";
            Console.Write($"Password for {user}: ");
            var pwd = ReadMasked();
            pwd.MakeReadOnly();
            _creds[env] = new SqlCredential(user, pwd);
        }
    }

    private static SecureString ReadMasked()
    {
        var s = new SecureString();
        for (ConsoleKeyInfo k; (k = Console.ReadKey(true)).Key != ConsoleKey.Enter;)
        {
            if (k.Key == ConsoleKey.Backspace) { if (s.Length > 0) { s.RemoveAt(s.Length - 1); Console.Write("\b \b"); } }
            else if (!char.IsControl(k.KeyChar)) { s.AppendChar(k.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return s;
    }

    private SqlConnection Connection(string env, string server, string db)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server, InitialCatalog = db, Encrypt = true,
            TrustServerCertificate = opt.TrustServerCertificate, ConnectTimeout = 15,
            // Separate name per environment = separate connection pool, so a connection opened as one
            // environment's account is never reused for another
            ApplicationName = $"DocLockAdmin-{env}"
        };
        if (_creds.TryGetValue(env, out var cred)) return new SqlConnection(b.ConnectionString, cred);
        b.IntegratedSecurity = true;
        return new SqlConnection(b.ConnectionString);
    }

    // Client databases per server for one environment (cached; all servers are checked in parallel)
    public async Task<List<ServerDatabases>> DiscoverAsync(string env, bool refresh)
    {
        var list = servers.For(env);
        if (list.Count == 0) throw new BadTargetException($"No servers for environment '{env}'.");
        if (!refresh && _cache.TryGetValue(env, out var c) && c.At > DateTime.UtcNow.AddMinutes(-opt.CacheMinutes)) return c.Result;

        var result = (await Task.WhenAll(list.Select(async server =>
        {
            try
            {
                var rows = await QueryAsync(new Target(env, server, "master"), Queries.ClientDatabases);
                var dbs = rows.Rows.Select(r => (string)r["name"]!)
                    .Where(d => !opt.ExcludeDatabases.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
                return new ServerDatabases(server, dbs, null);
            }
            catch (SqlException ex) { return new ServerDatabases(server, [], ex.Message); }
        }))).ToList();

        _cache[env] = (DateTime.UtcNow, result);
        return result;
    }

    // Only servers from Servers.csv and databases found by discovery can be targeted
    public async Task<Target> TargetAsync(string env, string server, string db)
    {
        var found = (await DiscoverAsync(env, false)).FirstOrDefault(s => s.Server.Equals(server, StringComparison.OrdinalIgnoreCase))
            ?? throw new BadTargetException($"Server '{server}' is not listed for {env}.");
        var name = found.Databases.FirstOrDefault(d => d.Equals(db, StringComparison.OrdinalIgnoreCase))
            ?? throw new BadTargetException($"Database '{db}' is not a client database on {server}. Try Refresh.");
        return new Target(env, found.Server, name);
    }

    // Login table lives in the auth database on every server of the environment
    public Target AuthTarget(string env, string server)
    {
        var match = servers.For(env).FirstOrDefault(s => s.Equals(server, StringComparison.OrdinalIgnoreCase))
            ?? throw new BadTargetException($"Server '{server}' is not listed for {env}.");
        return new Target(env, match, opt.AuthDatabase);
    }

    public record ServerLogins(string Server, List<string> Columns, List<Dictionary<string, object?>> Rows, string? Error);

    public async Task<List<ServerLogins>> FindLoginsAsync(string env, string prefix)
    {
        var list = servers.For(env);
        if (list.Count == 0) throw new BadTargetException($"No servers for environment '{env}'.");
        var pattern = Logins.LikePattern(prefix);
        return (await Task.WhenAll(list.Select(async server =>
        {
            try
            {
                var r = await QueryAsync(AuthTarget(env, server), Queries.FindLogins, ("@pattern", pattern));
                // Never send password hashes or other binary columns to the browser
                var cols = r.Columns.Where(c => r.Rows.All(row => row[c] is not byte[])).ToList();
                var rows = r.Rows.Select(row => cols.ToDictionary(c => c, c => row[c])).ToList();
                return new ServerLogins(server, cols, rows, null);
            }
            catch (Exception ex) when (ex is SqlException or BadTargetException) { return new ServerLogins(server, [], [], ex.Message); }
        }))).ToList();
    }

    public record DbUsers(string Server, string Db, List<Dictionary<string, object?>> Rows, string? Error);

    // Searches AL.UserIndex in every client database of the environment (database names differ per server)
    public async Task<List<DbUsers>> FindAppUsersAsync(string env, string prefix)
    {
        var pattern = Logins.LikePattern(prefix);
        var dbs = (await DiscoverAsync(env, false)).SelectMany(s => s.Databases.Select(d => new Target(env, s.Server, d))).ToList();
        using var gate = new SemaphoreSlim(10);   // don't open hundreds of connections at once
        var found = await Task.WhenAll(dbs.Select(async t =>
        {
            await gate.WaitAsync();
            try { return new DbUsers(t.Server, t.Db, (await QueryAsync(t, Queries.FindAppUsers, ("@pattern", pattern))).Rows, null); }
            catch (Exception ex) when (ex is SqlException or BadTargetException) { return new DbUsers(t.Server, t.Db, [], ex.Message); }
            finally { gate.Release(); }
        }));
        return found.Where(d => d.Rows.Count > 0 || d.Error != null).ToList();
    }

    public record QueryResult(List<string> Columns, List<Dictionary<string, object?>> Rows);

    public Task<QueryResult> QueryAsync(Target t, string query, params (string Name, object Value)[] args) =>
        AsAccount(t.Env, () => QueryCoreAsync(t, query, args));

    public Task<int> ExecuteAsync(Target t, string query, params (string Name, object Value)[] args) =>
        AsAccount(t.Env, () => ExecuteCoreAsync(t, query, args));

    private async Task<QueryResult> QueryCoreAsync(Target t, string query, (string Name, object Value)[] args)
    {
        await using var conn = Connection(t.Env, t.Server, t.Db);
        await conn.OpenAsync();
        await using var cmd = Command(conn, query, args);
        await using var reader = await cmd.ExecuteReaderAsync();
        var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
            rows.Add(cols.Select((c, i) => (c, v: reader.IsDBNull(i) ? null : reader.GetValue(i))).ToDictionary(x => x.c, x => x.v));
        return new QueryResult(cols, rows);
    }

    private async Task<int> ExecuteCoreAsync(Target t, string query, (string Name, object Value)[] args)
    {
        await using var conn = Connection(t.Env, t.Server, t.Db);
        await conn.OpenAsync();
        await using var cmd = Command(conn, query, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    private SqlCommand Command(SqlConnection conn, string query, (string Name, object Value)[] args)
    {
        var cmd = new SqlCommand(query, conn) { CommandTimeout = opt.QueryTimeoutSeconds };
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    public void Audit(Target t, string action, string subject, int rows)
    {
        var path = opt.AuditLog;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var by = _accounts.TryGetValue(t.Env, out var acct) ? $"{CurrentUser} as {acct}"
               : _creds.TryGetValue(t.Env, out var c) ? $"{CurrentUser} (sql:{c.UserId})"
               : CurrentUser;
        lock (_auditLock)
        {
            if (!File.Exists(path)) File.AppendAllText(path, "Time,RunBy,Environment,Server,Database,Action,Subject,RowsChanged\r\n");
            File.AppendAllText(path, string.Join(",", Q(DateTime.Now.ToString("s")), Q(by), Q(t.Env), Q(t.Server), Q(t.Db), Q(action), Q(subject), rows) + "\r\n");
        }
        Console.WriteLine($"{DateTime.Now:s} {action} {subject} on {t.Server}/{t.Db}: {rows} row(s)");
    }
}

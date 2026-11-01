using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TapeDrop.Catalog;

/// <summary>
/// Local SQLite catalog of what has been written to which tape, plus the saved backup jobs.
/// The catalog is a convenience: every tape also carries its own manifest, so this database
/// can always be rebuilt from the tapes themselves.
/// </summary>
public sealed class CatalogDb : IDisposable
{
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS tapes(
    id INTEGER PRIMARY KEY,
    label TEXT NOT NULL UNIQUE,
    first_seen_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS jobs(
    id INTEGER PRIMARY KEY,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    backup_job_id INTEGER NULL,
    tape_id INTEGER NOT NULL REFERENCES tapes(id),
    dest_folder TEXT NOT NULL,
    started_utc TEXT NOT NULL,
    finished_utc TEXT NOT NULL,
    verified INTEGER NOT NULL,
    bytes INTEGER NOT NULL,
    file_count INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS files(
    id INTEGER PRIMARY KEY,
    job_id INTEGER NOT NULL REFERENCES jobs(id),
    path TEXT NOT NULL,
    size INTEGER NOT NULL,
    sha256 TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_files_path ON files(path);
CREATE INDEX IF NOT EXISTS idx_files_job ON files(job_id);
CREATE TABLE IF NOT EXISTS backup_jobs(
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    source_path TEXT NOT NULL,
    verify INTEGER NOT NULL DEFAULT 1,
    eject INTEGER NOT NULL DEFAULT 0,
    last_run_utc TEXT NULL,
    last_tape_label TEXT NULL);";

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    /// <param name="path">Database file path, or ":memory:" for a throwaway catalog.</param>
    public CatalogDb(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Execute("PRAGMA foreign_keys=ON;");
        Execute(Schema);
    }

    public void Dispose() => _db.Dispose();

    // ---------- Job history ----------

    /// <summary>Records a finished job and all of its files. Returns the new job id.</summary>
    public long RecordJob(JobRecord job)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();

            long tapeId = GetOrCreateTape(job.TapeLabel, tx);

            long jobId;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO jobs(kind,name,backup_job_id,tape_id,dest_folder,started_utc,finished_utc,verified,bytes,file_count) " +
                    "VALUES($kind,$name,$bj,$tape,$dest,$start,$finish,$ver,$bytes,$count); " +
                    "SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$kind", job.Kind);
                cmd.Parameters.AddWithValue("$name", job.Name);
                cmd.Parameters.AddWithValue("$bj", (object?)job.BackupJobId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tape", tapeId);
                cmd.Parameters.AddWithValue("$dest", job.DestFolder);
                cmd.Parameters.AddWithValue("$start", ToText(job.StartedUtc));
                cmd.Parameters.AddWithValue("$finish", ToText(job.FinishedUtc));
                cmd.Parameters.AddWithValue("$ver", job.Verified ? 1 : 0);
                cmd.Parameters.AddWithValue("$bytes", job.Bytes);
                cmd.Parameters.AddWithValue("$count", job.Files.Count);
                jobId = Convert.ToInt64(cmd.ExecuteScalar());
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO files(job_id,path,size,sha256) VALUES($job,$path,$size,$sha)";
                var pJob = cmd.Parameters.Add("$job", SqliteType.Integer);
                var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
                var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
                var pSha = cmd.Parameters.Add("$sha", SqliteType.Text);
                foreach (var f in job.Files)
                {
                    pJob.Value = jobId;
                    pPath.Value = f.Path;
                    pSize.Value = f.Size;
                    pSha.Value = f.Sha256;
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return jobId;
        }
    }

    /// <summary>Finds files whose path contains the text (case-insensitive for ASCII).</summary>
    public IReadOnlyList<SearchHit> SearchFiles(string text, int limit = 200)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT t.label, j.name, f.path, f.size, j.finished_utc " +
                "FROM files f JOIN jobs j ON j.id=f.job_id JOIN tapes t ON t.id=j.tape_id " +
                "WHERE f.path LIKE $pattern ESCAPE '\\' " +
                "ORDER BY j.finished_utc DESC, f.path LIMIT $limit";
            cmd.Parameters.AddWithValue("$pattern", "%" + EscapeLike(text) + "%");
            cmd.Parameters.AddWithValue("$limit", limit);

            var hits = new List<SearchHit>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                hits.Add(new SearchHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), FromText(r.GetString(4))));
            return hits;
        }
    }

    // ---------- Backup job definitions ----------

    public long CreateBackupJob(string name, string sourcePath, bool verify = true, bool ejectWhenDone = false)
    {
        lock (_gate)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO backup_jobs(name,source_path,verify,eject) VALUES($n,$s,$v,$e); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$s", sourcePath);
                cmd.Parameters.AddWithValue("$v", verify ? 1 : 0);
                cmd.Parameters.AddWithValue("$e", ejectWhenDone ? 1 : 0);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("A backup job with that name already exists.", ex);
            }
        }
    }

    public IReadOnlyList<BackupJobDef> ListBackupJobs()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT id,name,source_path,verify,eject,last_run_utc,last_tape_label FROM backup_jobs ORDER BY name";
            var list = new List<BackupJobDef>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadBackupJob(r));
            return list;
        }
    }

    public BackupJobDef? GetBackupJob(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT id,name,source_path,verify,eject,last_run_utc,last_tape_label FROM backup_jobs WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadBackupJob(r) : null;
        }
    }

    /// <summary>Deletes the saved definition only; the history of past runs stays in the catalog.</summary>
    public void DeleteBackupJob(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM backup_jobs WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkBackupRun(long id, DateTime whenUtc, string tapeLabel)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE backup_jobs SET last_run_utc=$w, last_tape_label=$t WHERE id=$id";
            cmd.Parameters.AddWithValue("$w", ToText(whenUtc));
            cmd.Parameters.AddWithValue("$t", tapeLabel);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- Helpers ----------

    private long GetOrCreateTape(string label, SqliteTransaction tx)
    {
        using (var insert = _db.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO tapes(label, first_seen_utc) VALUES($l,$t)";
            insert.Parameters.AddWithValue("$l", label);
            insert.Parameters.AddWithValue("$t", ToText(DateTime.UtcNow));
            insert.ExecuteNonQuery();
        }
        using var select = _db.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT id FROM tapes WHERE label=$l";
        select.Parameters.AddWithValue("$l", label);
        return Convert.ToInt64(select.ExecuteScalar());
    }

    private static BackupJobDef ReadBackupJob(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt64(3) != 0,
        r.GetInt64(4) != 0,
        r.IsDBNull(5) ? null : FromText(r.GetString(5)),
        r.IsDBNull(6) ? null : r.GetString(6));

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string ToText(DateTime dt) => dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime FromText(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

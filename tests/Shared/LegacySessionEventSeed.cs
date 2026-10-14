using Microsoft.EntityFrameworkCore;
using Sem.Web.Core;
using Sem.Web.Data;

// Upgrade fixtures must write the actual old schema, not a future EF entity model.
// The current model adds columns which do not exist until the fixture's migration.
internal static class LegacySessionEventSeed
{
    public static async Task AddAsync(ApplicationDbContext db, SessionEvent item)
    {
        await db.SaveChangesAsync(); // retained identities / preceding fixture dependencies
        await db.Database.OpenConnectionAsync();
        var columns = new List<string>();
        await using (var info = db.Database.GetDbConnection().CreateCommand())
        {
            info.CommandText = "PRAGMA table_info('SessionEvents')";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }
        await using var insert = db.Database.GetDbConnection().CreateCommand();
        var names = new List<string>(); var values = new List<string>();
        foreach (var name in columns)
        {
            var property = typeof(SessionEvent).GetProperty(name)
                ?? throw new InvalidOperationException("Unknown legacy event column " + name);
            var parameter = insert.CreateParameter(); parameter.ParameterName = "@p" + names.Count;
            var value = property.GetValue(item);
            parameter.Value = value is bool flag ? (flag ? 1 : 0) : value ?? DBNull.Value;
            insert.Parameters.Add(parameter); names.Add("\"" + name.Replace("\"", "\"\"") + "\"");
            values.Add(parameter.ParameterName);
        }
        insert.CommandText = "INSERT INTO SessionEvents (" + string.Join(',', names) + ") VALUES (" + string.Join(',', values) + ")";
        await insert.ExecuteNonQueryAsync();
        db.Phases.AddRange(item.Phases); db.EventAdmins.AddRange(item.Admins);
    }
}

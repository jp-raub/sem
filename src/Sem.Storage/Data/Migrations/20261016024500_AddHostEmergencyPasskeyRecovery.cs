using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddHostEmergencyPasskeyRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "RecoveryMode", table: "PasskeyRecoveryOperations",
            type: "TEXT", nullable: false, defaultValue: "Administrator");
        migrationBuilder.AddColumn<string>(name: "ActorType", table: "PasskeyRecoveryOperations",
            type: "TEXT", nullable: false, defaultValue: "Administrator");
        migrationBuilder.AddColumn<string>(name: "ActorId", table: "PasskeyRecoveryOperations",
            type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "BackupId", table: "PasskeyRecoveryOperations",
            type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ActorType", table: "PasskeyRecoveryAudits",
            type: "TEXT", nullable: false, defaultValue: "Administrator");
        migrationBuilder.AddColumn<string>(name: "ActorId", table: "PasskeyRecoveryAudits",
            type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "BackupId", table: "PasskeyRecoveryAudits",
            type: "TEXT", nullable: true);

        // Backfill historical administrator actors without altering their audit meaning.
        migrationBuilder.Sql("UPDATE \"PasskeyRecoveryOperations\" SET \"ActorId\"=\"AdminId\";");
        migrationBuilder.Sql("UPDATE \"PasskeyRecoveryAudits\" SET \"ActorId\"=\"AdminId\";");

        migrationBuilder.DropCheckConstraint(name: "CK_PasskeyRecoveryOperation_State",
            table: "PasskeyRecoveryOperations");
        migrationBuilder.DropCheckConstraint(name: "CK_PasskeyRecoveryAudit_Action",
            table: "PasskeyRecoveryAudits");
        migrationBuilder.AlterColumn<string>(name: "AdminId", table: "PasskeyRecoveryOperations",
            type: "TEXT", nullable: true, oldClrType: typeof(string), oldType: "TEXT");
        migrationBuilder.AlterColumn<string>(name: "AdminId", table: "PasskeyRecoveryAudits",
            type: "TEXT", nullable: true, oldClrType: typeof(string), oldType: "TEXT");

        migrationBuilder.AddCheckConstraint(name: "CK_PasskeyRecoveryOperation_State",
            table: "PasskeyRecoveryOperations",
            sql: "\"Status\" IN ('Pending','HostPrepared','EmailConfirmed','Completed','Expired','Cancelled','Invalidated') AND \"RowVersion\">0");
        migrationBuilder.AddCheckConstraint(name: "CK_PasskeyRecoveryOperation_Actor",
            table: "PasskeyRecoveryOperations",
            sql: "((\"RecoveryMode\"='Administrator' AND \"ActorType\"='Administrator' AND \"AdminId\" IS NOT NULL AND \"ActorId\"=\"AdminId\" AND \"BackupId\" IS NULL) OR (\"RecoveryMode\"='HostEmergency' AND \"ActorType\"='HostOperator' AND \"AdminId\" IS NULL AND length(trim(\"ActorId\")) BETWEEN 1 AND 128 AND \"BackupId\" IS NOT NULL AND length(\"BackupId\")=64))");
        migrationBuilder.AddCheckConstraint(name: "CK_PasskeyRecoveryAudit_Action",
            table: "PasskeyRecoveryAudits",
            sql: "\"Action\" IN ('RecoveryStarted','RecoveryMailResent','RecoveryEmailConfirmed','RecoveryCompleted','RecoveryHostPrepared','RecoveryHostReset')");
        migrationBuilder.AddCheckConstraint(name: "CK_PasskeyRecoveryAudit_Actor",
            table: "PasskeyRecoveryAudits",
            sql: "((\"ActorType\"='Administrator' AND \"AdminId\" IS NOT NULL AND \"ActorId\"=\"AdminId\" AND \"BackupId\" IS NULL) OR (\"ActorType\"='HostOperator' AND \"AdminId\" IS NULL AND length(trim(\"ActorId\")) BETWEEN 1 AND 128 AND \"BackupId\" IS NOT NULL AND length(\"BackupId\")=64))");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Host recovery records cannot be safely downgraded without losing actor provenance.");
}

# INV-003 DXP pre-deployment duplicate gate (proposal)

This is a **manual, read-only preflight** for the SQLite database used by the DXP deployment. It is not an automated deployment step and does not repair data. The new filtered unique index on `Invitations.RequestId` will fail if a non-null request ID already appears more than once.

## Gate

1. Identify the exact DXP SQLite database using the deployment's approved procedure. Do not paste database paths, connection details, tokens, invitation links, or query output containing row data into chat or logs.
2. Take and verify the normal protected backup before any migration. Do not run this check against an actively changing database unless the deployment procedure guarantees a consistent read-only view.
3. Run the following aggregate-only query using an approved SQLite client opened in read-only mode. It returns only the number of duplicate `RequestId` groups; it never selects IDs, token hashes, protected tokens, or invitation links.

   ```sql
   SELECT COUNT(*)
   FROM (
       SELECT 1
       FROM Invitations
       WHERE RequestId IS NOT NULL
       GROUP BY RequestId
       HAVING COUNT(*) > 1
   );
   ```

   For the SQLite CLI, use its read-only option (for example, `sqlite3 -readonly "$DB_FILE"`), then enter the query. Keep the database filename private where required; do not use `.dump`, select duplicate rows, or enable query tracing.

4. **Continue only if the result is `0`.** Record only the gate outcome, timestamp, and operator in the deployment record.
5. If the result is non-zero, the gate fails: stop before applying the migration, record the count only, and escalate for an explicitly reviewed data-remediation plan. Do not delete, merge, rewrite, or backfill rows as part of this gate.
6. A zero count is only a prerequisite, not approval to deploy. Apply the migration separately under the existing change-control, backup, and verification procedure.

If the database cannot be opened read-only, the query fails, or its output is ambiguous, treat the gate as failed and stop. No DXP database was inspected or modified while preparing this proposal.
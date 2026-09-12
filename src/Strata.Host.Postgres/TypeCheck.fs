namespace Strata.Host.Postgres

open System
open Npgsql

/// Type-checking a statement by asking PostgreSQL to plan it.
///
/// Authority for: tier 3 of the compile-time-safety ladder
/// (`DF-STRATA-2026-8B60`).
///
/// ## What PREPARE decides that the catalog cannot
///
/// Tier 2 — does `customer.emial` exist — is a question about names, and the
/// semantic model answers it. Tier 3 is a question about TYPES:
///
///   - `WHERE total = 'abc'` where `total` is `numeric`
///   - `SELECT lower(created_at)` — no such function for that argument type
///   - `SELECT id FROM a JOIN b USING (id)` where `id` is ambiguous
///   - `SELECT a UNION SELECT b` where the two do not unify
///
/// Answering those means implementing PostgreSQL's type resolution, overload
/// selection and implicit-cast rules. `PREPARE x AS <statement>` parses,
/// resolves and PLANS the statement without executing it, and returns exactly
/// the error the server would have raised. This is the same bet that made
/// expression comparison work: ask the server rather than model its rules.
///
/// ## Nothing runs and nothing persists
///
/// One transaction, always rolled back, and `PREPARE` plans rather than
/// executes — no rows are read, written or locked beyond the ACCESS SHARE that
/// planning takes. Each statement gets a savepoint so one rejection does not
/// abort the rest.
///
/// ## What it cannot check, and says so
///
/// `PREPARE` accepts `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE` and
/// `VALUES` only. DDL and utility statements are not preparable at all, so a
/// file of them comes back NOT CHECKED rather than checked-and-passed. That
/// distinction is the whole `ER-008` discipline: a checker that answers "OK"
/// when it means "I could not tell" is worse than no checker, because people
/// stop looking.
module TypeCheck =

    /// What the server said about one statement.
    type Finding =
        { /// 1-based index of the statement in the file.
          Statement: int
          /// Byte offset of the statement in the file.
          Offset: int
          /// PostgreSQL's own message, unedited. Strata does not improve on it:
          /// the server's wording is what the author will find in a search.
          Message: string
          /// The five-character SQLSTATE, so a caller can distinguish an
          /// undefined function from a type mismatch without reading English.
          SqlState: string }

    /// A statement `PREPARE` will not take, and why. Not a pass and not a
    /// failure — a question that could not be asked.
    type NotChecked =
        { Statement: int
          Offset: int
          Reason: string }

    type Result' =
        { Findings: Finding list
          NotChecked: NotChecked list }

    /// `PREPARE` refuses a non-preparable statement with this SQLSTATE, and the
    /// refusal means "wrong kind of statement", not "wrong statement".
    [<Literal>]
    let private SyntaxErrorOrAccessRuleViolation = "42601"

    /// The statement kinds `PREPARE` accepts. Checked by keyword rather than by
    /// catching the server's refusal, so that a genuine syntax error in a SELECT
    /// is reported as a FINDING while `CREATE TABLE` is reported as not
    /// checkable — the two arrive with the same SQLSTATE and only the caller can
    /// tell them apart.
    let private preparableKeywords =
        [ "select"; "insert"; "update"; "delete"; "merge"; "values"; "with"; "table" ]

    let private firstKeyword (sql: string) =
        match ShadowNormalisation.firstWord sql with
        | Some word -> word.ToLowerInvariant()
        | None -> ""

    let isPreparable (sql: string) =
        preparableKeywords |> List.contains (firstKeyword sql)

    /// Type-check each statement. `statements` is `(index, offset, sql)`.
    let check
        (connectionString: string)
        (statements: (int * int * string) list)
        : Microsoft.FSharp.Core.Result<Result', string> =

        if List.isEmpty statements then
            Ok { Findings = []; NotChecked = [] }
        else

        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use transaction = connection.BeginTransaction()

            try
                let exec (sql: string) =
                    use command = new NpgsqlCommand(sql, connection, transaction)
                    command.ExecuteNonQuery() |> ignore

                let checkOne (index: int, offset: int, sql: string) =
                    if not (isPreparable sql) then
                        Microsoft.FSharp.Core.Error
                            { Statement = index
                              Offset = offset
                              Reason =
                                sprintf
                                    "PREPARE takes only SELECT, INSERT, UPDATE, DELETE, MERGE and VALUES, so a %s statement cannot be type-checked this way"
                                    ((firstKeyword sql).ToUpperInvariant()) }
                    else

                    let savepoint = "sp_" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    let name = "_strata_tc_" + Guid.NewGuid().ToString("N").Substring(0, 12)

                    try
                        exec (sprintf "SAVEPOINT %s" savepoint)
                        exec (sprintf "PREPARE %s AS %s" name (sql.TrimEnd().TrimEnd(';')))
                        // Planned without complaint. Discard it so a second
                        // statement cannot collide with the name.
                        exec (sprintf "DEALLOCATE %s" name)
                        Ok None
                    with
                    | :? PostgresException as ex ->
                        try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()

                        Ok(
                            Some
                                { Statement = index
                                  Offset = offset
                                  Message = ex.MessageText
                                  SqlState = ex.SqlState }
                        )
                    | ex ->
                        try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()

                        Microsoft.FSharp.Core.Error
                            { Statement = index
                              Offset = offset
                              Reason = sprintf "the server could not be asked (%s)" ex.Message }

                let results = statements |> List.map checkOne

                // ALWAYS. There is no success path that commits.
                transaction.Rollback()

                Ok
                    { Findings = results |> List.choose (function Ok f -> f | _ -> None)
                      NotChecked = results |> List.choose (function Microsoft.FSharp.Core.Error n -> Some n | _ -> None) }
            with ex ->
                try transaction.Rollback() with _ -> ()
                Microsoft.FSharp.Core.Error ex.Message
        with ex ->
            Microsoft.FSharp.Core.Error ex.Message

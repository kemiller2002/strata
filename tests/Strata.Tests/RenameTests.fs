module Strata.Tests.RenameTests

open Xunit
open Strata.Host.Files

/// Tests for reading `-- strata:renamed_from` (Q-004).
///
/// §86: a rename and a drop-plus-add produce IDENTICAL desired states, so
/// intent can only be declared, never inferred. ER-010 makes guessing forbidden
/// rather than merely unwise — guess wrong and you either destroy a table or
/// silently keep one that should have gone.
///
/// Comments are read from the RAW TEXT because libpg_query discards them; they
/// never reach the parse tree.

let private read = RenameAnnotations.read

[<Fact>]
let ``an annotation before the column list renames the object`` () =
    let result =
        read "-- strata:renamed_from sales.legacy_orders\nCREATE TABLE sales.orders (id bigint);"

    Assert.Equal(Some "sales.legacy_orders", result.Object)
    Assert.Empty result.Columns

[<Fact>]
let ``an annotation on a column line renames that column`` () =
    let result =
        read """CREATE TABLE sales.orders (
    id     bigint NOT NULL,
    total  numeric(12,2), -- strata:renamed_from amount
    status text
);"""

    Assert.Equal(None, result.Object)
    Assert.Equal<(string * string) list>([ "amount", "total" ], result.Columns)

[<Fact>]
let ``object and column renames are read together`` () =
    let result =
        read """-- strata:renamed_from sales.legacy
CREATE TABLE sales.orders (
    total numeric(12,2), -- strata:renamed_from amount
    label text -- strata:renamed_from name
);"""

    Assert.Equal(Some "sales.legacy", result.Object)
    Assert.Equal<(string * string) list>([ "amount", "total"; "name", "label" ], result.Columns)

[<Fact>]
let ``a file with no annotation declares no renames`` () =
    let result = read "CREATE TABLE sales.orders (id bigint);"

    Assert.Equal(None, result.Object)
    Assert.Empty result.Columns

[<Fact>]
let ``an annotation on a constraint line is ignored, not misattributed`` () =
    // A rename Strata cannot place is one it must not act on. Attaching it to
    // the constraint's first word would invent a column rename nobody asked for.
    let result =
        read """CREATE TABLE sales.orders (
    id bigint,
    CONSTRAINT pk PRIMARY KEY (id) -- strata:renamed_from old_pk
);"""

    Assert.Empty result.Columns

[<Fact>]
let ``the annotation's own text is not mistaken for a column`` () =
    // The line is split at the comment before looking for a column, so
    // `strata:renamed_from` can never be read as a column name.
    let result =
        read """CREATE TABLE sales.orders (
    -- strata:renamed_from amount
    total numeric(12,2)
);"""

    Assert.Empty result.Columns

[<Fact>]
let ``a quoted old name is read`` () =
    let result = read "-- strata:renamed_from \"Sales\".\"LegacyOrders\"\nCREATE TABLE sales.orders (id bigint);"

    Assert.Equal(Some "\"Sales\".\"LegacyOrders\"", result.Object)

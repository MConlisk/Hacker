/// In-memory tables (Document 7 §13.11) for cross-referencing manifests, timestamps and tide tables. Tables are
/// immutable: every query returns a new table. Cells hold int, float, string, bool or null. Costs: rows × columns / 8.
module Std.Data;

import Std.Text;

enum Aggregate { Count, Sum, Average, Min, Max, First, Last, CountDistinct }

class Row {
    int Index { get; }
    /// Raises IndexError for an unknown column.
    object? Get(string column);
    string GetString(string column);
    int GetInt(string column);
    float GetFloat(string column);
    bool IsNull(string column);
    string ToString();
}

class Table {
    Table(string[] columns);
    /// Parses CSV with a header row; cells that look like ints or floats become numbers.
    static Table FromCsv(string text);
    /// An array of objects; columns are the union of their keys in first-seen order.
    static Table FromJson(JsonValue rows);
    string[] Columns { get; }
    int RowCount { get; }
    Row Row(int index);
    Row[] Rows { get; }
    /// A new table with one more row (values in column order).
    Table AddRow(params object?[] values);
    object? Get(int row, string column);
    object?[] Column(string column);

    Table Where(fn(Row) -> bool predicate);
    /// Keeps (and reorders) the named columns.
    Table Select(params string[] columns);
    /// A new column computed from each row.
    Table WithColumn(string name, fn(Row) -> object? compute);
    Table Rename(string column, string newName);
    /// Stable sort; nulls first.
    Table OrderBy(string column);
    Table OrderByDescending(string column);
    Table Limit(int count);
    Table Distinct();
    /// Inner join on equal key values; clashing right-hand column names get a "right." prefix.
    Table Join(Table other, string leftColumn, string rightColumn);
    Table LeftJoin(Table other, string leftColumn, string rightColumn);
    /// One row per distinct key, with the aggregate in a column named "<op>(<valueColumn>)" (e.g. "Sum(price)").
    Table GroupBy(string keyColumn, string valueColumn, Aggregate op);
    /// A single aggregate over a column.
    object? Aggregate(string column, Aggregate op);
    /// Rows keyed by rowColumn, one column per distinct colColumn value, cells aggregated from valueColumn.
    Table Pivot(string rowColumn, string colColumn, string valueColumn, Aggregate op);
    Table Concat(Table other);

    string ToCsv();
    /// An array of objects.
    string ToJson();
    /// Aligned text table (first 50 rows).
    string ToString();
}

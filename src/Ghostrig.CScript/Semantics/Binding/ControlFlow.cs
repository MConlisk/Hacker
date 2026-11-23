namespace Ghostrig.CScript.Semantics.Binding;

/// <summary>Reachability analysis over bound statements ("not all code paths return a value").</summary>
internal static class ControlFlow
{
    /// <summary>True when execution can fall off the end of <paramref name="statement"/>.</summary>
    public static bool CanCompleteNormally(BoundStatement statement) => statement switch
    {
        BoundBlock block => block.Statements.All(CanCompleteNormally),
        BoundReturn or BoundThrow or BoundYieldBreak or BoundBreak or BoundContinue => false,
        BoundExpressionStatement { Expression: BoundThrowExpression } => false,
        BoundIf { Else: null } => true,
        BoundIf ifStmt => CanCompleteNormally(ifStmt.Then) || CanCompleteNormally(ifStmt.Else!),
        BoundWhile loop => !IsConstantTrue(loop.Condition) || ContainsBreak(loop.Body),
        BoundFor loop => (loop.Condition is not null && !IsConstantTrue(loop.Condition)) || ContainsBreak(loop.Body),
        BoundSwitch sw => !sw.Sections.Any(s => s.Labels.Any(l => l.Pattern is null || l.Pattern is BoundDiscardPattern))
                          || sw.Sections.Any(s => s.Statements.Any(ContainsBreak) || s.Statements.All(CanCompleteNormally)),
        BoundTry tryStmt => (CanCompleteNormally(tryStmt.Body) || tryStmt.Catches.Any(c => CanCompleteNormally(c.Body)))
                            && (tryStmt.Finally is null || CanCompleteNormally(tryStmt.Finally)),
        _ => true,
    };

    private static bool IsConstantTrue(BoundExpression condition) => condition.ConstantValue is true;

    /// <summary>Does the statement contain a <c>break</c> that targets the enclosing loop (not a nested loop/switch)?</summary>
    private static bool ContainsBreak(BoundStatement statement) => statement switch
    {
        BoundBreak => true,
        BoundBlock block => block.Statements.Any(ContainsBreak),
        BoundIf ifStmt => ContainsBreak(ifStmt.Then) || (ifStmt.Else is not null && ContainsBreak(ifStmt.Else)),
        BoundTry tryStmt => ContainsBreak(tryStmt.Body) || tryStmt.Catches.Any(c => ContainsBreak(c.Body))
                            || (tryStmt.Finally is not null && ContainsBreak(tryStmt.Finally)),
        _ => false,
    };
}

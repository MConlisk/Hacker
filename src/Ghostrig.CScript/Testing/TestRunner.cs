using System.Collections.Immutable;
using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Testing;

/// <summary>The outcome of one test (or one <c>@TestCase</c> row).</summary>
public sealed record TestResult(string Module, string Name, bool Passed, string Message, long Steps)
{
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")} {Module}.{Name} ({Steps} steps){(Passed ? string.Empty : ": " + Message)}";
}

/// <summary>
/// Discovers and runs CScript unit tests (Doc 7 §14.4): functions marked <c>@Test</c>, or <c>@TestCase(args…)</c> once per
/// attribute, with optional <c>@Setup</c> / <c>@Teardown</c> functions in the same module. Every test runs in a fresh
/// virtual machine (globals re-initialised), so tests cannot leak state into each other.
/// </summary>
public static class TestRunner
{
    public static ImmutableArray<TestResult> Run(Compilation compilation, VmOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var image = ProgramImage.Link(compilation);
        var results = ImmutableArray.CreateBuilder<TestResult>();
        foreach (var module in compilation.Modules)
        {
            var functions = module.Members.OfType<MethodSymbol>().ToList();
            var setup = functions.FirstOrDefault(f => Has(f, "Setup"));
            var teardown = functions.FirstOrDefault(f => Has(f, "Teardown"));
            foreach (var function in functions)
            {
                var cases = Attributes(function).Where(a => a.Name == "TestCase").ToList();
                if (Has(function, "Test"))
                {
                    results.Add(RunOne(image, module.Name, function, function.Name, [], setup, teardown, options));
                }

                foreach (var testCase in cases)
                {
                    var arguments = testCase.Arguments.Select(Literal).ToArray();
                    var label = $"{function.Name}({string.Join(", ", arguments.Select(a => a.Object is string s ? $"\"{s}\"" : a.ToString()))})";
                    results.Add(arguments.Length == function.Parameters.Length
                        ? RunOne(image, module.Name, function, label, arguments, setup, teardown, options)
                        : new TestResult(module.Name, label, false, $"@TestCase has {arguments.Length} argument(s); {function.Name} takes {function.Parameters.Length}.", 0));
                }
            }
        }

        return results.ToImmutable();
    }

    private static IEnumerable<AttributeSyntax> Attributes(MethodSymbol method) =>
        method.Syntax is FunctionDecl decl ? decl.Attributes : [];

    private static bool Has(MethodSymbol method, string name) => Attributes(method).Any(a => a.Name == name);

    private static Value Literal(Expr expression) => expression switch
    {
        LiteralExpr { Kind: LiteralKind.Char, Value: var ch } => Value.FromChar(Convert.ToInt64(ch, System.Globalization.CultureInfo.InvariantCulture)),
        LiteralExpr { Value: long l } => Value.FromInt(l),
        LiteralExpr { Value: double d } => Value.FromFloat(d),
        LiteralExpr { Value: bool b } => Value.FromBool(b),
        LiteralExpr { Value: string s } => Value.FromString(s),
        UnaryExpr { Operator: UnaryOperator.Negate, Operand: LiteralExpr { Value: long l } } => Value.FromInt(-l),
        UnaryExpr { Operator: UnaryOperator.Negate, Operand: LiteralExpr { Value: double d } } => Value.FromFloat(-d),
        _ => Value.Null,
    };

    private static TestResult RunOne(ProgramImage image, string module, MethodSymbol function, string label, Value[] arguments,
        MethodSymbol? setup, MethodSymbol? teardown, VmOptions? options)
    {
        var vm = new VirtualMachine(image, new VirtualClockHost(), options);
        try
        {
            vm.Initialize();
            if (setup is not null)
            {
                vm.Invoke(image.GetCallable(setup));
            }

            var result = vm.Invoke(image.GetCallable(function), arguments);
            if (function.ReturnType == PrimitiveType.Bool && !result.AsBool)
            {
                return new TestResult(module, label, false, "The test returned false.", vm.Steps);
            }

            return new TestResult(module, label, true, string.Empty, vm.Steps);
        }
        catch (ScriptException e)
        {
            return new TestResult(module, label, false, VirtualMachine.DescribeError(e.Error), vm.Steps);
        }
        catch (InvalidOperationException e)
        {
            return new TestResult(module, label, false, e.Message, vm.Steps);
        }
        finally
        {
            if (teardown is not null)
            {
                try
                {
                    vm.Invoke(image.GetCallable(teardown));
                }
                catch (ScriptException)
                {
                    // A failing teardown never hides the test's own result.
                }
            }
        }
    }
}

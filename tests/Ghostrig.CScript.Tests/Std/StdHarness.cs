using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Tests.Std;

/// <summary>Compiles a test module against the full standard library and runs it on a virtual clock.</summary>
internal sealed class StdProgram
{
    private StdProgram(ProgramImage image, VirtualMachine vm, VirtualClockHost host) => (Image, Vm, Host) = (image, vm, host);

    public ProgramImage Image { get; }
    public VirtualMachine Vm { get; }
    public VirtualClockHost Host { get; }

    public static StdProgram Load(string source, VmOptions? options = null)
    {
        var compilation = Compilation.Create(SyntaxTree.Parse(source, "Test.csc"));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        var image = ProgramImage.Link(compilation);
        Assert.Empty(image.LinkErrors);
        var host = new VirtualClockHost();
        var vm = new VirtualMachine(image, host, options);
        vm.Initialize();
        return new StdProgram(image, vm, host);
    }

    public Value Call(string function, params Value[] args) => Vm.Invoke(Image.FindFunction("Test", function)!, args);

    public string Text(string function = "Main") => Call(function).AsString;

    /// <summary>Runs a string-returning Main written against the given imports.</summary>
    public static string Run(string imports, string body) => Load($"module Test;\n{imports}\n{body}").Text();

    public static string ErrorOf(string imports, string body)
    {
        var ex = Assert.Throws<ScriptException>(() => Run(imports, body));
        return VirtualMachine.DescribeError(ex.Error);
    }
}

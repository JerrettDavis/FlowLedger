using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using FluentAssertions;

namespace FlowLedger.Architecture.Tests;

/// <summary>
/// #173: a test never constructs a wire contract by position. Contracts DTOs and requests, the Application request/DTO records
/// and (in Web.Tests) the Web client DTOs are built through the builders in <c>FlowLedger.TestSupport/Builders</c> (and, for
/// Web-only types, <c>FlowLedger.Web.Tests/Builders</c>), so adding a field to a contract changes one builder instead of every
/// test that happens to build one.
///
/// The scan reads the IL of the built test assemblies (every <c>newobj</c> of a contract record outside the builders), so it sees
/// every spelling of a construction: <c>new X(...)</c>, a target-typed <c>new(...)</c> in a return, an argument or a collection
/// expression, and fully-qualified names alike. The Architecture test project references the other test projects (build order
/// only) so they are always built before this runs. <see cref="AllowList"/> may only shrink: an entry that no longer matches a
/// construction fails the test, so a converted class cannot quietly keep its exemption.
/// </summary>
public sealed class PositionalDtoConstructionTests
{
    /// <summary>"Assembly|TestClass|ContractType" of a construction still allowed. Empty: do not add entries, add or extend a builder.</summary>
    private static readonly HashSet<string> AllowList = new(StringComparer.Ordinal);

    private static readonly string[] BuilderNamespaces = ["FlowLedger.TestSupport", "FlowLedger.Web.Tests.Builders"];

    [Fact]
    public void Tests_build_contracts_through_builders_not_positional_constructors()
    {
        var violations = Scan();

        violations.Select(v => v.Key).Where(k => !AllowList.Contains(k)).Should().BeEmpty(
            "contracts are built with a FlowLedger.TestSupport builder (AccountDtoBuilder.A().Named(...).Build()), not positionally; "
            + "add the builder or the missing With... method instead of an allow-list entry");
        AllowList.Where(a => violations.All(v => v.Key != a)).Should().BeEmpty("the allow-list may only shrink; remove entries whose construction was converted");
    }

    [Fact]
    public void The_scan_sees_the_test_assemblies_and_the_contract_types()
    {
        // Guards the guard: a missing assembly or an empty type set would let the scan above pass vacuously.
        TestAssemblies().Select(Path.GetFileNameWithoutExtension).Should().Contain(
            ["FlowLedger.Application.Tests", "FlowLedger.Api.Tests", "FlowLedger.Web.Tests", "FlowLedger.Infrastructure.Tests"]);
        (SharedContracts().Count + WebContracts().Count).Should().BeGreaterThan(100);
    }

    private sealed record Violation(string Assembly, string TestClass, string Contract)
    {
        public string Key => $"{Assembly}|{TestClass}|{Contract}";
    }

    private static List<Violation> Scan()
    {
        var shared = SharedContracts();
        var web = WebContracts();
        var found = new HashSet<Violation>();
        foreach (var dll in TestAssemblies())
        {
            var assembly = Path.GetFileNameWithoutExtension(dll);
            using var stream = new MemoryStream(File.ReadAllBytes(dll));
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                var outer = type;
                while (!outer.GetDeclaringType().IsNil)
                {
                    outer = reader.GetTypeDefinition(outer.GetDeclaringType());
                }

                var outerName = reader.GetString(outer.Namespace) + "." + reader.GetString(outer.Name);
                if (BuilderNamespaces.Any(n => outerName.StartsWith(n, StringComparison.Ordinal)))
                {
                    continue;
                }

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    foreach (var constructed in ConstructedTypes(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!, reader))
                    {
                        if (shared.Contains(constructed) || (web.Contains(constructed) && assembly == "FlowLedger.Web.Tests"))
                        {
                            found.Add(new Violation(assembly, outerName, constructed));
                        }
                    }
                }
            }
        }

        return [.. found.OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>Full names of the types whose constructor (with at least one argument) the IL calls with <c>newobj</c>.</summary>
    private static IEnumerable<string> ConstructedTypes(byte[] il, MetadataReader reader)
    {
        var position = 0;
        while (position < il.Length)
        {
            ushort code = il[position++];
            if (code == 0xFE)
            {
                code = (ushort)(0xFE00 | il[position++]);
            }

            if (!OpCodeTable.TryGetValue(code, out var opcode))
            {
                yield break;
            }

            if (code == OpCodes.Newobj.Value && TryResolveConstructor(reader, BitConverter.ToInt32(il, position), out var type))
            {
                yield return type;
            }

            position += OperandSize(opcode, il, position);
        }
    }

    private static bool TryResolveConstructor(MetadataReader reader, int token, out string type)
    {
        type = "";
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MemberReference)
        {
            var member = reader.GetMemberReference((MemberReferenceHandle)handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                return false;
            }

            var signature = reader.GetBlobReader(member.Signature);
            signature.ReadByte();
            if (signature.ReadCompressedInteger() == 0)
            {
                return false;
            }

            var typeReference = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            type = reader.GetString(typeReference.Namespace) + "." + reader.GetString(typeReference.Name);
            return true;
        }

        return false;
    }

    private static readonly Dictionary<ushort, OpCode> OpCodeTable = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => (ushort)o.Value);

    private static int OperandSize(OpCode opcode, byte[] il, int position) => opcode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => 4,
    };

    /// <summary>The built test assemblies next to this one (same configuration), E2E included when it was built.</summary>
    private static List<string> TestAssemblies()
    {
        // .../tests/FlowLedger.Architecture.Tests/bin/<Configuration>/net10.0/
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = output.Name;
        var configuration = output.Parent!.Name;
        var tests = output.Parent.Parent!.Parent!.Parent!.FullName;
        return Directory.EnumerateDirectories(tests, "FlowLedger.*.Tests")
            .Where(d => !d.EndsWith("FlowLedger.Architecture.Tests", StringComparison.Ordinal))
            .Select(d => Path.Combine(d, "bin", configuration, framework, Path.GetFileName(d) + ".dll"))
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>Contracts records plus the Application request/DTO records: built the same way from any test project.</summary>
    private static HashSet<string> SharedContracts()
    {
        var contracts = typeof(FlowLedger.Contracts.AccountDto).Assembly.GetExportedTypes().Where(IsRecord);
        var application = typeof(FlowLedger.Application.Abstractions.IDomainEventDispatcher).Assembly.GetExportedTypes()
            .Where(t => IsRecord(t) && (t.Name.EndsWith("Request", StringComparison.Ordinal) || t.Name.EndsWith("Dto", StringComparison.Ordinal)));
        return [.. contracts.Concat(application).Select(t => t.FullName!)];
    }

    /// <summary>The Web client's own DTO records; only checked in Web.Tests (other projects have unrelated types of the same name).</summary>
    private static HashSet<string> WebContracts() =>
        [.. typeof(FlowLedger.Web.ApiClient.FlowLedgerApiClient).Assembly.GetExportedTypes()
            .Where(t => IsRecord(t) && t.Namespace == "FlowLedger.Web.ApiClient")
            .Select(t => t.FullName!)];

    private static bool IsRecord(Type t) => t.IsClass && !t.IsAbstract && t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;
}

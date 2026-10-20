using System.Reflection;
using ModelContextProtocol.Server;

namespace BiatecMCPTests
{
    /// <summary>
    /// The tool tests call the C# methods directly, so none of them proves the MCP SDK can actually register a tool (its JSON
    /// schema is generated from the method signature at runtime, when a client lists tools). This builds every
    /// <c>[McpServerTool]</c> through the SDK the same way <c>WithToolsFromAssembly</c> does, so a signature the SDK can't
    /// express - or a duplicate tool name - fails here instead of at the first <c>tools/list</c> in production.
    /// </summary>
    [TestFixture]
    public class McpToolRegistrationTests
    {
        private static IEnumerable<MethodInfo> ToolMethods() =>
            typeof(BiatecMCP.MCP.BiatecMCP).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

        [Test]
        public void EveryTool_BuildsThroughTheSdk_WithASchemaAndAUniqueName()
        {
            var names = new List<string>();
            foreach (var method in ToolMethods())
            {
                var tool = McpServerTool.Create(method, static _ => null!);

                Assert.That(tool.ProtocolTool.Name, Is.Not.Empty, method.Name);
                Assert.That(tool.ProtocolTool.Description, Is.Not.Empty, method.Name);
                Assert.That(tool.ProtocolTool.InputSchema.ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Object), method.Name);
                names.Add(tool.ProtocolTool.Name);
            }

            Assert.That(names, Is.Unique);
            Assert.That(names, Has.Count.GreaterThanOrEqualTo(21));
        }

        [Test]
        public void CreateSolanaTransaction_IsRegisteredWithItsParametersInTheSchema()
        {
            var method = ToolMethods().Single(m => m.Name == "CreateSolanaTransaction");

            var tool = McpServerTool.Create(method, static _ => null!);

            Assert.That(tool.ProtocolTool.Name, Is.EqualTo("createSolanaTransaction"));
            var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
            foreach (var parameter in new[] { "receiverAddress", "amountLamports", "network", "seedAddress", "slot" })
            {
                Assert.That(properties.TryGetProperty(parameter, out _), Is.True, parameter);
            }

            var required = tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(required, Is.EquivalentTo(new[] { "receiverAddress", "amountLamports", "network" }));
        }
    }
}

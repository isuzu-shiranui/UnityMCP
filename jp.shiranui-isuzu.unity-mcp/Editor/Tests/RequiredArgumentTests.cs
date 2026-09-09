using System.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// An argument written with a C# default is optional in the schema, and an agent reads the
    /// schema to decide what to send. Where the handler then refuses the call, every omission is a
    /// refused round trip that the agent had no way to foresee.
    /// </summary>
    /// <remarks>
    /// The C# default cannot simply be removed: these parameters are followed by other defaulted
    /// ones, so <c>[McpArg(Required = true)]</c> is what puts them back in the schema's
    /// <c>required</c> array.
    /// </remarks>
    public sealed class RequiredArgumentTests
    {
        private static readonly (string Tool, string Argument)[] Refused =
        {
            ("asset_info", "path"),
            ("asset_create_folder", "path"),
            ("asset_move", "path"),
            ("asset_move", "destination"),
            ("asset_delete", "path"),
            ("asset_reimport", "path"),
            ("reflect_read", "path"),
            ("reflect_find_type", "name"),
            ("render_compare", "before"),
            ("render_compare", "after"),
            ("build_player", "output_path"),
            ("scene_open", "path"),
            ("prefab_create", "path"),
            ("prefab_instantiate", "path"),
            ("gameobject_add_component", "component_type"),
            ("gameobject_remove_component", "component_type"),
            ("input_pointer", "from"),
            ("animator_add_layer", "name"),
            ("animator_remove_layer", "layer"),
            ("animator_add_state", "layer"),
            ("animator_add_state", "name"),
            ("animator_remove_state", "layer"),
            ("animator_remove_state", "state"),
            ("animator_set_state", "layer"),
            ("animator_set_state", "state"),
            ("animator_add_transition", "layer"),
            ("animator_add_transition", "to_state"),
            ("animator_remove_transition", "layer"),
            ("animator_add_parameter", "name"),
            ("animator_remove_parameter", "name"),
            ("timeline_create", "asset_path"),
            ("timeline_create_track", "type"),
            ("timeline_create_clip", "track"),
            ("timeline_edit_clip", "track"),
            ("timeline_set_track", "track"),
            ("timeline_delete", "track"),
        };

        /// <summary>
        /// Arguments a handler wants only in some situations. Marking one of these required would
        /// refuse a call that works — a drag needs 'to', a move does not.
        /// </summary>
        private static readonly (string Tool, string Argument)[] Conditional =
        {
            ("material_set", "value"),
            ("material_read", "slot"),
            ("material_set", "slot"),
            ("input_pointer", "to"),
            ("input_pointer", "scroll_delta"),
        };

        private static ToolCatalog catalog;

        [OneTimeSetUp]
        public void BuildCatalog()
        {
            catalog = ToolCatalog.Build();
        }

        private static string[] RequiredOf(string tool)
        {
            var descriptor = catalog.Tools.FirstOrDefault(t => t.Name == tool);
            Assert.That(descriptor, Is.Not.Null, $"'{tool}' is not in the catalog.");

            return descriptor.InputSchema["required"]?.Select(t => t.ToString()).ToArray()
                   ?? new string[0];
        }

        [Test]
        public void EveryArgumentAHandlerRefusesToWorkWithoutIsDeclaredRequired()
        {
            foreach (var (tool, argument) in Refused)
            {
                var required = RequiredOf(tool);

                Assert.That(required, Does.Contain(argument),
                    $"'{tool}' refuses the call without '{argument}', so the schema has to say so.");
            }
        }

        [Test]
        public void AnArgumentWantedOnlySometimesIsNotDeclaredRequired()
        {
            foreach (var (tool, argument) in Conditional)
            {
                var required = RequiredOf(tool);

                Assert.That(required, Does.Not.Contain(argument),
                    $"'{argument}' is wanted only in some situations, so requiring it refuses calls that work.");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    public sealed class RecoveryResourceNode
    {
        public ResourceKind Kind { get; set; }
        public string Id { get; set; } = string.Empty;
        public string[] Parents { get; set; } = Array.Empty<string>();
    }

    public sealed class RecoveryScopeDecision
    {
        public string Scope { get; set; } = "System";
        public ResourceKind Kind { get; set; } = ResourceKind.System;
        public bool IsSystemWide { get; set; } = true;
        public string Reason { get; set; } = string.Empty;
        public string[] AffectedResources { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Resource topology is the only place that converts observations into a
    /// recovery scope.  Producers cannot request StopAll or process restart.
    /// Unknown/unproven dependencies deliberately expand to System.
    /// </summary>
    public sealed class RecoveryResourceGraph
    {
        private readonly Dictionary<string, RecoveryResourceNode> _nodes =
            new Dictionary<string, RecoveryResourceNode>(StringComparer.OrdinalIgnoreCase);

        public void Add(RecoveryResourceNode node)
        {
            if (node == null || node.Kind == ResourceKind.Unknown ||
                string.IsNullOrWhiteSpace(node.Id))
                throw new ArgumentException("RecoveryResourceNodeInvalid", nameof(node));
            _nodes[node.Id] = new RecoveryResourceNode
            {
                Kind = node.Kind,
                Id = node.Id,
                Parents = (node.Parents ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }

        public RecoveryScopeDecision Resolve(FaultObservation observation)
        {
            if (observation?.IsStructurallyValid() != true)
                throw new ArgumentException("FaultObservationInvalid", nameof(observation));
            if (!observation.ScopeProven || observation.ResourceKind == ResourceKind.System ||
                !_nodes.TryGetValue(observation.ResourceId, out var node) ||
                node.Kind != observation.ResourceKind)
                return SystemScope("FaultScopeUnproven");
            if (!observation.SafetyChainHealthy ||
                (!observation.OutputOffConfirmed &&
                 observation.Severity == FaultSeverity.SafetyCritical))
                return SystemScope("SharedSafetyProofInsufficient");

            var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                node.Id
            };
            foreach (var parent in node.Parents) resources.Add(parent);
            return new RecoveryScopeDecision
            {
                Scope = node.Id,
                Kind = node.Kind,
                IsSystemWide = false,
                Reason = "MinimumFaultDomainProven",
                AffectedResources = resources.OrderBy(value => value).ToArray()
            };
        }

        public static RecoveryResourceGraph CreateDefaultEpbGraph()
        {
            var graph = new RecoveryResourceGraph();
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.System, Id = "System" });
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.DaqDevice, Id = "DAQ:Dev1", Parents = new[] { "System" } });
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.DaqDevice, Id = "DAQ:Dev2", Parents = new[] { "System" } });
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.HydraulicGroup, Id = "Hydraulic:1", Parents = new[] { "DAQ:Dev1", "System" } });
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.HydraulicGroup, Id = "Hydraulic:2", Parents = new[] { "DAQ:Dev2", "System" } });
            for (var group = 1; group <= 4; group++)
                graph.Add(new RecoveryResourceNode
                {
                    Kind = ResourceKind.ElectricalGroup,
                    Id = "Power:" + group.ToString(CultureInfo.InvariantCulture),
                    Parents = new[] { "System" }
                });
            for (var channel = 1; channel <= 12; channel++)
            {
                var daq = channel <= 6 ? "DAQ:Dev1" : "DAQ:Dev2";
                var hydraulic = channel <= 6 ? "Hydraulic:1" : "Hydraulic:2";
                var power = "Power:" + (((channel - 1) / 3) + 1)
                    .ToString(CultureInfo.InvariantCulture);
                graph.Add(new RecoveryResourceNode
                {
                    Kind = ResourceKind.Channel,
                    Id = "Channel:" + channel.ToString(CultureInfo.InvariantCulture),
                    Parents = new[] { power, hydraulic, daq, "System" }
                });
            }
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.Storage, Id = "Storage", Parents = new[] { "System" } });
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.EngineHost, Id = "EngineHost", Parents = new[] { "System" } });
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.UserInterface, Id = "UserInterface" });
            graph.Add(new RecoveryResourceNode { Kind = ResourceKind.SafetyChain, Id = "SafetyChain", Parents = new[] { "System" } });
            return graph;
        }

        private static RecoveryScopeDecision SystemScope(string reason)
        {
            return new RecoveryScopeDecision
            {
                Scope = "System",
                Kind = ResourceKind.System,
                IsSystemWide = true,
                Reason = reason,
                AffectedResources = new[] { "System" }
            };
        }
    }
}

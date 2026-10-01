using System.Text.Json;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

internal static class QaRunnerCheck
{
    public static async Task Run(QaContext c)
    {
        using (c.UseStagePhase("Player fixture preparation"))
            await c.Measure("same stage purpose", () => Task.CompletedTask);
        await c.Measure("same stage purpose", () => Task.CompletedTask);
        QaContext.Require(c.Current.Stages[0].Name != c.Current.Stages[1].Name &&
            c.Current.Stages[0].Name.Contains("Player fixture preparation") &&
            c.Current.Stages[1].Name.Contains("Test workflow"),
            "Stage names collided or fixture preparation phase leaked into the test workflow.");
        QaWarningDiagnosticsCheck.Run(c);
        c.Current.Evidence["trashPolicyChecks"] = QaTrashPolicyCheck.RunSelfChecks();
        foreach (var kind in new[] { QaStageKind.Load, QaStageKind.Randomisation, QaStageKind.FileTransfer })
        {
            var limit = QaPerformance.Limit(kind)!.Value;
            QaContext.Require(!QaPerformance.Exceeded(kind, limit) && QaPerformance.Exceeded(kind, limit + 0.01),
                "Timing boundary is incorrect: " + kind);
        }
        QaContext.Require(!QaPerformance.Exceeded(QaStageKind.Render, 100000), "Render/PNG readback acquired a timing limit.");
        var synthetic = new QaResult("synthetic", "threshold enforcement") { Status = "passed" };
        synthetic.Stages.Add(new QaStage("synthetic load", 1500.01, 0, 0, 0, 0, 0, 0, 0, QaStageKind.Load));
        QaPerformance.ApplyWarnings(synthetic);
        QaContext.Require(synthetic.Status == "warning" && synthetic.Error is null &&
            synthetic.PerformanceWarnings.Single().Contains("synthetic load"), "Slow operation was not classified as a warning.");
        synthetic.Status = "failed";
        synthetic.Error = "Retained functional error";
        QaPerformance.ApplyWarnings(synthetic);
        QaContext.Require(synthetic.Status == "failed" && synthetic.Error == "Retained functional error",
            "Performance warning concealed a functional failure.");

        var flattened = Path.Combine(c.Output, c.Current.Id, "flattened");
        Directory.CreateDirectory(flattened);
        for (var index = 0; index < 2; index++)
        {
            var directory = Path.Combine(c.Output, c.Current.Id, "mesh-" + index);
            Directory.CreateDirectory(directory);
            var bytes = new byte[44];
            BitConverter.TryWriteBytes(bytes.AsSpan(12, 4), 0.01f);
            BitConverter.TryWriteBytes(bytes.AsSpan(28, 4), 0.01f);
            BitConverter.TryWriteBytes(bytes.AsSpan(38, 2), (ushort)1);
            BitConverter.TryWriteBytes(bytes.AsSpan(40, 2), (ushort)2);
            File.WriteAllBytes(Path.Combine(directory, "head.bin"), bytes);
            File.WriteAllText(Path.Combine(directory, "head.gltf"), """
                {"asset":{"version":"2.0"},"buffers":[{"uri":"head.bin","byteLength":44}],
                "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962},{"buffer":0,"byteOffset":36,"byteLength":6,"target":34963}],
                "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[0.01,0.01,0]},
                {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],
                "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
                "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
                """);
            File.WriteAllText(Path.Combine(directory, "head.gltf.mfe.json"), "{}");
            var renamed = QaOutputFiles.NamespaceMeshExport(c, directory);
            foreach (var file in renamed.Values)
            {
                var destination = Path.Combine(flattened, Path.GetFileName(file));
                File.Move(file, destination);
                c.Current.Artifacts.Add(destination);
            }
        }
        foreach (var path in Directory.GetFiles(flattened, "*.gltf"))
        {
            var mesh = new MorphFaceInterchangeService().ReadMesh(path);
            QaContext.Require(mesh.Positions.Length == 3 && mesh.Indices.Length == 3 && File.Exists(path + ".mfe.json"),
                "Flattened glTF lost geometry or its companion sidecar.");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var uri = json.RootElement.GetProperty("buffers")[0].GetProperty("uri").GetString()!;
            QaContext.Require(File.Exists(Path.Combine(flattened, Uri.UnescapeDataString(uri))), "Renamed glTF buffer URI is broken.");
        }
        var names = Directory.GetFiles(c.Output, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray();
        QaContext.Require(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length, "Output basenames collide.");
        c.Current.Evidence["checks"] = new[] { "1500/5000ms strict boundaries", "render exemption", "performance warning retains functional error",
            "two identically named mesh exports flattened without collisions", "production decoder reads renamed glTF buffers", "sidecar filenames preserved",
            "unique stage names and scoped fixture phase", "exact saved-export warning reconciliation" };
    }
}

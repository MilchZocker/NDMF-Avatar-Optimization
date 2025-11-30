#if UNITY_EDITOR
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(MilchZocker.AvatarOptimizer.AvatarOptimizerPlugin))]

namespace MilchZocker.AvatarOptimizer
{
    public class AvatarOptimizerPlugin : Plugin<AvatarOptimizerPlugin>
    {
        public override string QualifiedName => "dev.milchzocker.ndmf-avatar-optimisation";
        public override string DisplayName => "Avatar Optimiser";

        protected override void Configure()
        {
            // Run the optimizer after the merge plugin ("dev.milchzocker.ndmf-merge").
            // If that plugin isn't installed, NDMF will just ignore this constraint.
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("dev.milchzocker.ndmf-merge")
                .Run("Optimise Avatar", ctx =>
                {
                    var optimizer = ctx.AvatarRootTransform.GetComponent<AvatarOptimizer>();
                    if (optimizer == null) return;

                    Debug.Log($"[AvatarOptimizer] Found Avatar Optimizer component on {ctx.AvatarRootObject.name}");

                    var processor = new AvatarOptimizationProcessor(ctx, optimizer);
                    processor.Process();
                });
        }
    }
}
#endif

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Ladder
{
    /// <summary>
    /// A ladder standing in the world: its line from the foot to the top end, and its look (aluminium, two sections like an
    /// extension ladder). It has no collider: it blocks neither bricks nor aiming at their places, and the mobile
    /// scaffold drives through it; climbing is computed from the player's position.
    /// </summary>
    internal sealed class LadderInstance
    {
        private static readonly Color Aluminium = new Color(0.76f, 0.78f, 0.80f);
        private static Material material;

        internal readonly LadderSpec Spec;
        internal readonly Vector3 Foot;
        /// <summary>Along the ladder (up), out of its face (away from the wall), and across it.</summary>
        internal readonly Vector3 Up, Front, Side;
        internal readonly Vector3 Dir;
        private readonly GameObject visual;

        internal LadderInstance(LadderSpec spec)
        {
            Spec = spec;
            Dir = spec.Dir;
            Foot = LadderGeometry.Foot(spec);
            Up = Dir * LadderGeometry.Cos + Vector3.up * LadderGeometry.Sin;
            Front = -Dir * LadderGeometry.Sin + Vector3.up * LadderGeometry.Cos;
            Side = Vector3.Cross(Vector3.up, Dir).normalized;
            visual = BuildVisual();
        }

        internal Vector3 Top => Foot + Up * Spec.length;
        internal bool HasExit => Spec.hasExit;
        internal Vector3 Exit => Spec.Exit;

        internal float ParamOf(Vector3 p) => Vector3.Dot(p - Foot, Up);
        internal float FrontOf(Vector3 p) => Vector3.Dot(p - Foot, Front);
        internal float SideOf(Vector3 p) => Vector3.Dot(p - Foot, Side);

        /// <summary>The player's feet when on the ladder at s meters from the foot, offset in front of it.</summary>
        internal Vector3 Path(float s, float offset) => Foot + Up * s + Front * offset;

        /// <summary>Highest place on the ladder: feet just over the top edge where one steps off, otherwise 1.6 m below
        /// the top end (hands on the last rungs).</summary>
        internal float HighestParam(float offset)
        {
            float s = HasExit ? (Spec.top + 0.05f - Foot.y - Front.y * offset) / Up.y : Spec.length - 1.6f;
            return Mathf.Clamp(s, 0f, Spec.length);
        }

        /// <summary>Does the view ray pass within 0.35 m of the ladder, within maxDistance?</summary>
        internal bool IsAimedAt(Ray ray, float maxDistance)
        {
            const int steps = 40;
            for (int i = 0; i <= steps; i++)
            {
                Vector3 p = Foot + Up * (Spec.length * i / steps);
                float along = Vector3.Dot(p - ray.origin, ray.direction);
                if (along > 0f && along < maxDistance && Vector3.Distance(ray.origin + ray.direction * along, p) < 0.35f)
                {
                    return true;
                }
            }
            return false;
        }

        internal void Destroy()
        {
            if (visual != null)
            {
                Object.Destroy(visual);
            }
        }

        private GameObject BuildVisual()
        {
            var go = new GameObject("Ladder (mod)");
            go.transform.SetPositionAndRotation(Foot, Quaternion.LookRotation(Front, Up));   // local y along, z out of its face
            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = PoleStripe(primitive.GetComponent<MeshFilter>().sharedMesh);
            Object.Destroy(primitive);
            var parts = new List<CombineInstance>();
            void Box(Vector3 center, Vector3 size) =>
                parts.Add(new CombineInstance { mesh = cube, transform = Matrix4x4.TRS(center, Quaternion.identity, size) });
            void Section(float from, float to, float width, float z)
            {
                foreach (float x in new[] { -width / 2f, width / 2f })
                {
                    Box(new Vector3(x, (from + to) / 2f, z), new Vector3(0.045f, to - from, 0.07f));          // rails
                }
                for (float y = from + 0.25f; y < to - 0.08f; y += 0.28f)
                {
                    Box(new Vector3(0f, y, z), new Vector3(width - 0.045f, 0.032f, 0.032f));                   // rungs
                }
            }
            float length = Spec.length;
            if (length <= 4.5f)
            {
                Section(0f, length, 0.44f, 0.035f);
            }
            else
            {
                float l1 = (length + 0.9f) / 2f;                 // two sections overlapping 0.9 m; the upper one rests on the
                Section(0f, l1, 0.46f, 0.105f);                  // wall's edge, the lower one stands 7 cm further out
                Section(length - l1, length, 0.38f, 0.035f);
            }
            var mesh = new Mesh { name = "Ladder" };
            mesh.CombineMeshes(parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = Material();
            return go;
        }

        /// <summary>
        /// The game's scaffold material is a texture atlas of its mobile scaffold (measured for the ExtraBuildings mod): the
        /// boxes take their UVs from a pole stripe of it (its colour map is swapped for plain aluminium, the stripe keeps its
        /// normal map), as the ExtraBuildings scaffold does. Without that a box shows the whole atlas.
        /// </summary>
        private static Mesh PoleStripe(Mesh source)
        {
            var mesh = Object.Instantiate(source);
            var area = new Rect(0.555f, 0.10f, 0.055f, 0.75f);
            Vector2[] uv = mesh.uv;
            for (int i = 0; i < uv.Length; i++)
            {
                uv[i] = new Vector2(area.xMin + uv[i].x * area.width, area.yMin + uv[i].y * area.height);
            }
            mesh.uv = uv;
            return mesh;
        }

        /// <summary>Plain aluminium: the game's scaffold material without its texture, else any lit material tinted.</summary>
        private static Material Material()
        {
            if (material != null)
            {
                return material;
            }
            Material[] all = Resources.FindObjectsOfTypeAll<Material>();
            Material source = all.FirstOrDefault(m => m.name == "M_Scaffolding")
                ?? all.FirstOrDefault(m => m.shader != null && m.shader.name.Contains("Lit") && m.HasProperty("_BaseColor"));
            material = source != null ? new Material(source) { name = "Ladder_Aluminium" } : new Material(Shader.Find("Standard"));
            foreach (string map in new[] { "_BaseMap", "_MainTex" })
            {
                if (material.HasProperty(map))
                {
                    material.SetTexture(map, Texture2D.whiteTexture);
                }
            }
            foreach (string tint in new[] { "_BaseColor", "_Color" })
            {
                if (material.HasProperty(tint))
                {
                    material.SetColor(tint, Aluminium);
                }
            }
            Plugin.Log.LogInfo($"Ladder material from '{(source != null ? source.name : "Standard")}'.");
            return material;
        }
    }
}

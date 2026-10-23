using System.Collections.Generic;
using System.IO;
using ECM2;
using Newtonsoft.Json;
using UnityEngine;

namespace Ladder
{
    /// <summary>A ladder as kept per world: where it leans against which wall, how long it is, where one steps off.</summary>
    internal sealed class LadderSpec
    {
        /// <summary>Point of the wall face at the floor the ladder stands on (x, floor height, z).</summary>
        public float[] face;
        /// <summary>Horizontal direction from the foot towards the wall (x, z).</summary>
        public float[] dir;
        /// <summary>Top surface of the wall above the face, if within reach.</summary>
        public bool hasTop;
        public float top;
        public float length;
        /// <summary>Standing point on the top surface, when there is room to step off there.</summary>
        public bool hasExit;
        public float[] exit;

        internal Vector3 Face => new Vector3(face[0], face[1], face[2]);
        internal Vector3 Dir => new Vector3(dir[0], 0f, dir[1]);
        internal Vector3 Exit => hasExit ? new Vector3(exit[0], exit[1], exit[2]) : Vector3.zero;
    }

    /// <summary>
    /// Leans the ladder against the wall the player looks at, like a real one at 75 degrees (rule of thumb 4:1): its top
    /// reaches about 0.9 m over the wall's top edge, so one can step off, or it rests against the wall with its top when
    /// the wall is higher than the ladder is long.
    /// </summary>
    internal static class LadderGeometry
    {
        internal const float Angle = 75f;
        internal const float MinLength = 2f;
        private const float OverTop = 0.9f;
        private const float RailGap = 0.045f;      // the rails' back side keeps this from the wall

        internal static readonly float Sin = Mathf.Sin(Angle * Mathf.Deg2Rad);
        internal static readonly float Cos = Mathf.Cos(Angle * Mathf.Deg2Rad);

        /// <summary>Everything solid except the players (and objects that ignore raycasts).</summary>
        internal static int Mask
        {
            get
            {
                int mask = ~(1 << 2);
                foreach (string layer in new[] { "LocalPlayer", "ClientPlayer" })
                {
                    int l = LayerMask.NameToLayer(layer);
                    if (l >= 0)
                    {
                        mask &= ~(1 << l);
                    }
                }
                return mask;
            }
        }

        internal static bool TryFromAim(Ray ray, Character character, float maxLength, out LadderSpec spec, out string why)
        {
            spec = null;
            if (!Physics.Raycast(ray, out RaycastHit hit, 6f, Mask, QueryTriggerInteraction.Ignore))
            {
                why = "Look at a wall within 6 m to set up the ladder.";
                return false;
            }
            var n = new Vector3(hit.normal.x, 0f, hit.normal.z);
            if (n.magnitude < 0.7f)
            {
                why = "Look at a wall, not at a floor or a ceiling.";
                return false;
            }
            Vector3 dir = -n.normalized;
            float floor = character.GetPosition().y;
            spec = new LadderSpec { face = new[] { hit.point.x, floor, hit.point.z }, dir = new[] { dir.x, dir.z } };
            // The floor under the foot decides; measured twice, because the foot moves with the floor's height
            for (int pass = 0; pass < 2; pass++)
            {
                FindTop(spec, hit.point, maxLength);
                if (spec.hasTop && spec.top < spec.face[1] + 0.8f)
                {
                    why = "Nothing to climb here: the wall is lower than 0.8 m.";
                    return false;
                }
                spec.length = spec.hasTop ? Mathf.Clamp((spec.top + OverTop - spec.face[1]) / Sin, MinLength, maxLength) : maxLength;
                Vector3 foot = Foot(spec);
                if (!Physics.Raycast(foot + Vector3.up * 0.6f, Vector3.down, out RaycastHit ground, 1.6f, Mask, QueryTriggerInteraction.Ignore))
                {
                    why = "No floor for the ladder's foot here.";
                    return false;
                }
                if (Mathf.Abs(ground.point.y - spec.face[1]) < 0.02f)
                {
                    break;
                }
                spec.face[1] = ground.point.y;
            }
            FindExit(spec, character);
            why = null;
            return true;
        }

        /// <summary>The same ladder at another length (it slides along the wall, its angle stays).</summary>
        internal static bool TryResize(LadderSpec old, float length, Character character, out LadderSpec spec, out string why)
        {
            spec = new LadderSpec { face = (float[])old.face.Clone(), dir = (float[])old.dir.Clone(), hasTop = old.hasTop, top = old.top, length = length };
            if (!Physics.Raycast(Foot(spec) + Vector3.up * 0.6f, Vector3.down, out RaycastHit ground, 1.6f, Mask, QueryTriggerInteraction.Ignore)
                || Mathf.Abs(ground.point.y - spec.face[1]) > 0.3f)
            {
                why = "No floor for the ladder's foot at that length.";
                return false;
            }
            FindExit(spec, character);
            why = null;
            return true;
        }

        /// <summary>Highest surface in the wall's column just behind the face, if the ladder can reach it.</summary>
        private static void FindTop(LadderSpec spec, Vector3 aim, float maxLength)
        {
            Vector3 column = aim + spec.Dir * 0.1f;
            float reach = spec.face[1] + maxLength * Sin + 0.2f;
            var start = new Vector3(column.x, reach, column.z);
            spec.hasTop = false;
            if (reach <= aim.y || Physics.CheckSphere(start, 0.03f, Mask, QueryTriggerInteraction.Ignore))
            {
                return;                                    // the wall goes on above the ladder's reach
            }
            spec.hasTop = true;
            spec.top = Physics.Raycast(start, Vector3.down, out RaycastHit hit, reach - aim.y + 0.05f, Mask, QueryTriggerInteraction.Ignore)
                ? hit.point.y : aim.y;
        }

        /// <summary>Where the rails' back side touches the wall: the top edge, or the ladder's top below it.</summary>
        internal static float ContactHeight(LadderSpec spec)
        {
            float topEnd = spec.face[1] + spec.length * Sin;
            return spec.hasTop ? Mathf.Min(spec.top, topEnd) : topEnd;
        }

        internal static Vector3 Foot(LadderSpec spec)
        {
            float d = (ContactHeight(spec) - spec.face[1]) * Cos / Sin + RailGap;
            return spec.Face - spec.Dir * d;
        }

        /// <summary>A standing place on the top surface 0.6 to 1.2 m behind the face, with room for the player, at most
        /// 1 m below the top edge (a roof behind a parapet); only when the ladder reaches 0.5 m over the edge.</summary>
        private static void FindExit(LadderSpec spec, Character character)
        {
            spec.hasExit = false;
            if (!spec.hasTop || spec.face[1] + spec.length * Sin < spec.top + 0.5f)
            {
                return;
            }
            float r = character.characterMovement.radius, h = character.characterMovement.height;
            Vector3 dir = spec.Dir;
            foreach (float depth in new[] { 0.6f, 0.9f, 1.2f })
            {
                Vector3 p = spec.Face + dir * depth;
                var from = new Vector3(p.x, spec.top + 1f, p.z);
                if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, 2f, Mask, QueryTriggerInteraction.Ignore) || hit.point.y < spec.top - 1f)
                {
                    continue;
                }
                Vector3 stand = hit.point + Vector3.up * 0.02f;
                if (Physics.CheckCapsule(stand + Vector3.up * (r + 0.05f), stand + Vector3.up * (h - r), r - 0.02f, Mask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                spec.hasExit = true;
                spec.exit = new[] { stand.x, stand.y, stand.z };
                return;
            }
        }
    }

    /// <summary>The ladders per world slot, in the mod's own file next to its config.</summary>
    internal sealed class LadderStore
    {
        private readonly string path;
        private readonly Dictionary<string, LadderSpec> worlds;

        internal LadderStore(string path)
        {
            this.path = path;
            try
            {
                worlds = File.Exists(path) ? JsonConvert.DeserializeObject<Dictionary<string, LadderSpec>>(File.ReadAllText(path)) : null;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Ladder file unreadable, starting empty: " + e.Message);
            }
            worlds = worlds ?? new Dictionary<string, LadderSpec>();
        }

        internal LadderSpec Get(string slot) => worlds.TryGetValue(slot, out LadderSpec spec) ? spec : null;

        internal void Set(string slot, LadderSpec spec)
        {
            if (spec == null)
            {
                worlds.Remove(slot);
            }
            else
            {
                worlds[slot] = spec;
            }
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(worlds, Formatting.Indented));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Ladder file not written: " + e.Message);
            }
        }
    }
}

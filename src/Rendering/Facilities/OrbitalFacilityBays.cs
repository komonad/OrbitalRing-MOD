using ProjectOrbitalRing.Utils;
using UnityEngine;
using static ProjectOrbitalRing.Rendering.OrbitalFacilityAttachments;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalFacilityBays
    {
        private readonly struct BayFrame
        {
            internal readonly Vector3 Center;
            internal readonly Vector3 Along;
            internal readonly Vector3 Up;
            internal readonly Vector3 Outward;
            internal readonly Vector3 Station;
            internal readonly float Length;

            internal BayFrame(Vector3 first, Vector3 last, Vector3 station)
            {
                Center = (first + last) * 0.5f;
                Along = (last - first).normalized;
                Up = Center.normalized;
                Vector3 across = Vector3.Cross(Along, Up).normalized;
                Outward = Vector3.Dot(Center - station, across) >= 0 ? across : -across;
                Station = station;
                Length = Vector3.Distance(first, last);
            }

            internal Vector3 Point(float outward, float height, float along = 0)
            {
                return Center + Outward * outward + Up * height + Along * along;
            }
        }

        internal static bool Beam(OrbitalAttachmentMesh[] surfaces, PlanetFactory factory, int ring,
            float longitude, int entityId)
        {
            if (!TryFrame(factory, ring, 1, longitude, ProtoID.I粒子加速轨道, entityId, 1.4f, out BayFrame frame))
            {
                return false;
            }

            AcceleratorHousing(surfaces, frame);
            bool controlCenter = factory.entityPool[entityId].protoId == ProtoID.I星环对撞机;
            if (controlCenter)
            {
                ColliderPortSaddle(surfaces, factory, entityId, frame);
                ColliderTransfer(surfaces, factory, entityId, frame);
            }

            return true;
        }

        private static void AcceleratorHousing(OrbitalAttachmentMesh[] surfaces, BayFrame frame)
        {
            OrbitalAttachmentMesh armor = surfaces[(int)Surface.Armor];
            OrbitalAttachmentMesh recess = surfaces[(int)Surface.Recess];
            OrbitalAttachmentMesh metal = surfaces[(int)Surface.Metal];
            OrbitalAttachmentMesh lights = surfaces[(int)Surface.BeamLight];
            armor.Tube(frame.Center, frame.Along, frame.Up, 1.16f, 1.255f, frame.Length, 48, true);
            recess.Tube(frame.Center, frame.Along, frame.Up, 1.252f, 1.27f, 0.055f, 48);
            foreach (float sign in new[] { -1f, 1f })
            {
                Vector3 endpoint = frame.Point(0, 0, sign * (frame.Length * 0.5f - 0.11f));
                metal.Tube(endpoint, frame.Along, frame.Up, 1.09f, 1.28f, 0.22f, 48);
                recess.Tube(endpoint + frame.Along * (sign * 0.14f), frame.Along, frame.Up, 1.09f, 1.18f, 0.06f, 48);
                foreach (float angle in new[] { 0, Mathf.PI * 2 / 3, Mathf.PI * 4 / 3 })
                {
                    Vector3 lug = endpoint + frame.Up * (Mathf.Cos(angle) * 1.29f);
                    lug += frame.Outward * (Mathf.Sin(angle) * 1.29f);
                    armor.BeveledBox(lug, frame.Along, frame.Up, frame.Outward,
                        new Vector3(0.40f, 0.22f, 0.26f), 0.04f);
                }

                Vector3 firstLight = frame.Point(1.20f, sign * 0.40f, -frame.Length * 0.43f);
                Vector3 lastLight = frame.Point(1.20f, sign * 0.40f, frame.Length * 0.43f);
                lights.Cylinder(firstLight, lastLight, 0.016f);
            }
        }

        internal static bool Power(OrbitalAttachmentMesh[] surfaces, PlanetFactory factory, int ring,
            float longitude, int entityId)
        {
            if (!TryFrame(factory, ring, 2, longitude, ProtoID.I星环电网组件, entityId, 0.95f, out BayFrame frame))
            {
                return false;
            }

            OrbitalAttachmentMesh armor = surfaces[(int)Surface.Armor];
            OrbitalAttachmentMesh recess = surfaces[(int)Surface.Recess];
            OrbitalAttachmentMesh metal = surfaces[(int)Surface.Metal];
            OrbitalAttachmentMesh ceramic = surfaces[(int)Surface.Ceramic];
            OrbitalAttachmentMesh conductor = surfaces[(int)Surface.Conductor];
            OrbitalAttachmentMesh lights = surfaces[(int)Surface.PowerLight];
            OrbitalAttachmentMesh panel = surfaces[(int)Surface.PowerPanel];
            recess.BeveledBox(frame.Center, frame.Outward, frame.Up, frame.Along,
                new Vector3(1.80f, 1.15f, frame.Length), 0.18f);
            armor.BeveledBox(frame.Point(-0.02f, 0.35f), frame.Outward, frame.Up, frame.Along,
                new Vector3(1.64f, 0.50f, frame.Length * 0.92f), 0.15f);
            armor.BeveledBox(frame.Point(-0.02f, -0.40f), frame.Outward, frame.Up, frame.Along,
                new Vector3(1.58f, 0.38f, frame.Length * 0.92f), 0.12f);
            foreach (float sign in new[] { -1f, 1f })
            {
                metal.BeveledBox(frame.Point(0.90f, sign * 0.37f), frame.Outward, frame.Up, frame.Along,
                    new Vector3(0.045f, 0.07f, frame.Length * 0.80f), 0.02f);
                Vector3 endpoint = frame.Point(0, 0, sign * (frame.Length * 0.5f - 0.08f));
                armor.BeveledBox(endpoint, frame.Outward, frame.Up, frame.Along,
                    new Vector3(1.84f, 1.19f, 0.18f), 0.20f);
                foreach (float lateral in new[] { -0.3645f, 0, 0.3645f })
                {
                    Vector3 feedthrough = endpoint + frame.Outward * lateral + frame.Along * (sign * 0.28f);
                    conductor.Tube(feedthrough, frame.Along, frame.Up, 0.075f, 0.12f, 0.65f, 20);
                    ceramic.Tube(feedthrough, frame.Along, frame.Up, 0.11f, 0.15f, 0.40f, 24);
                    metal.Tube(feedthrough - frame.Along * (sign * 0.27f), frame.Along, frame.Up,
                        0.10f, 0.18f, 0.08f, 24);
                }
            }

            panel.Panel(frame.Point(0.934f, 0), frame.Along, frame.Up, frame.Outward, frame.Length * 0.90f, 0.74f);
            foreach (float sign in new[] { -1f, 1f })
            {
                Vector3 statusLamp = frame.Point(0.95f, 0.18f, sign * frame.Length * 0.46f);
                metal.Tube(statusLamp, frame.Outward, frame.Up, 0.04f, 0.075f, 0.045f, 20);
                lights.Tube(statusLamp + frame.Outward * 0.025f, frame.Outward, frame.Up, 0, 0.044f, 0.025f, 20);
            }

            Vector3 towerUp = frame.Station.normalized;
            Vector3 outward = Vector3.ProjectOnPlane(frame.Outward, towerUp).normalized;
            float terminalHeight = Vector3.Dot(frame.Center - frame.Station, towerUp) + 3.25f;
            var towerSurface = new OrbitalTrackIntersection.TowerSurface(factory, entityId);
            float terminalRadius = towerSurface.Radius(terminalHeight, outward);
            armor.ArcBand(frame.Station + towerUp * terminalHeight, towerUp, outward,
                terminalRadius - 0.05f, terminalRadius + 0.20f, 0.58f, 20 * Mathf.Deg2Rad, 12);
            foreach (float lateral in new[] { -0.3645f, 0, 0.3645f })
            {
                Vector3 lateralOffset = frame.Along * (lateral * 1.4f);
                float radius = MountingDepth(towerSurface, terminalHeight, outward, lateralOffset,
                    terminalRadius + 0.20f);
                Vector3 terminal = frame.Station + towerUp * terminalHeight + lateralOffset + outward * radius;
                Vector3 contact = frame.Point(0.65f, 0.64f, lateral * 1.4f);
                Vector3[] busbar = TransferCurve(contact, contact + towerUp * 0.65f + outward * 0.25f,
                    terminal + outward * 0.60f, terminal);
                conductor.Sweep(busbar, frame.Along, 0.055f);
                Vector3 bushingAxis = (busbar[5] - busbar[3]).normalized;
                ceramic.Tube(busbar[4], bushingAxis, frame.Along, 0.055f, 0.10f, 0.22f, 24);
                armor.BeveledBox(terminal - outward * 0.03f, frame.Along, towerUp, outward,
                    new Vector3(0.35f, 0.42f, 0.24f), 0.06f);
                metal.Tube(contact, towerUp, frame.Along, 0.055f, 0.12f, 0.13f, 20);
                lights.Tube(terminal + outward * 0.115f, outward, towerUp, 0, 0.035f, 0.035f, 16);
            }

            return true;
        }

        private static void ColliderPortSaddle(OrbitalAttachmentMesh[] surfaces, PlanetFactory factory,
            int entityId, BayFrame frame)
        {
            Vector3 towerUp = frame.Station.normalized;
            Vector3 outward = Vector3.ProjectOnPlane(frame.Outward, towerUp).normalized;
            float height = Vector3.Dot(frame.Center - frame.Station, towerUp) + 2.8f;
            var towerSurface = new OrbitalTrackIntersection.TowerSurface(factory, entityId);
            float radius = towerSurface.Radius(height, outward);
            Vector3 center = frame.Station + towerUp * height;
            OrbitalAttachmentMesh armor = surfaces[(int)Surface.Armor];
            OrbitalAttachmentMesh recess = surfaces[(int)Surface.Recess];
            float halfAngle = 55 * Mathf.Deg2Rad;
            armor.ArcBand(center, towerUp, outward, radius - 0.08f, radius + 0.20f, 0.86f, halfAngle);
            foreach (float sign in new[] { -1f, 1f })
            {
                recess.ArcBand(center + towerUp * (sign * 0.28f), towerUp, outward,
                    radius + 0.18f, radius + 0.22f, 0.07f, halfAngle);
            }
        }

        private static void ColliderTransfer(OrbitalAttachmentMesh[] surfaces, PlanetFactory factory,
            int entityId, BayFrame frame)
        {
            OrbitalAttachmentMesh armor = surfaces[(int)Surface.Armor];
            OrbitalAttachmentMesh recess = surfaces[(int)Surface.Recess];
            OrbitalAttachmentMesh metal = surfaces[(int)Surface.Metal];
            Vector3 towerUp = frame.Station.normalized;
            Vector3 outward = Vector3.ProjectOnPlane(frame.Outward, towerUp).normalized;
            float trackHeight = Vector3.Dot(frame.Center - frame.Station, towerUp);
            var towerSurface = new OrbitalTrackIntersection.TowerSurface(factory, entityId);
            float portHeight = trackHeight + 2.8f;
            float saddleRadius = towerSurface.Radius(portHeight, outward) + 0.20f;
            foreach (bool injection in new[] { true, false })
            {
                float side = injection ? 1 : -1;
                float height = portHeight;
                Vector3 lateralOffset = frame.Along * (side * 0.85f);
                float radius = MountingDepth(towerSurface, height, outward, lateralOffset, saddleRadius);
                Vector3 root = frame.Station + towerUp * height + lateralOffset + outward * radius;
                recess.Tube(root + outward * 0.23f, outward, towerUp, 0, 0.38f, 0.46f, 32);
                metal.Tube(root + outward * 0.08f, outward, towerUp, 0.32f, 0.45f, 0.11f, 32);
                armor.Tube(root + outward * 0.54f, outward, towerUp, 0.15f, injection ? 0.27f : 0.31f, 0.38f, 32);
                metal.Tube(root + outward * 0.70f, outward, towerUp, 0.16f, 0.30f, 0.09f, 32);
                foreach (float angle in new[] { 0, Mathf.PI * 2 / 3, Mathf.PI * 4 / 3 })
                {
                    Vector3 fastener = root + outward * 0.15f + frame.Along * (Mathf.Cos(angle) * 0.37f);
                    fastener += towerUp * (Mathf.Sin(angle) * 0.37f);
                    recess.Tube(fastener, outward, towerUp, 0, 0.048f, 0.055f, 8);
                }

                Vector3 towerPort = root + outward * 0.72f;
                float spacing = Mathf.Min(3.2f, frame.Length * 0.38f);
                Vector3 ringPort = frame.Point(1.04f, 0.92f, side * spacing);
                Vector3 radial = (frame.Outward * 1.04f + frame.Up * 0.92f).normalized;
                Vector3 socketSide = Vector3.Cross(radial, frame.Along).normalized;
                armor.BeveledBox(ringPort - radial * 0.19f, frame.Along, socketSide, radial,
                    new Vector3(1.12f, 0.54f, 0.37f), 0.065f);
                recess.Tube(ringPort, frame.Along, frame.Up, 0.15f, 0.225f, 0.48f, 24);
                metal.Tube(ringPort - frame.Along * (side * 0.24f), frame.Along, frame.Up, 0.15f, 0.25f, 0.07f, 24);
                float controlLength = Mathf.Min(spacing * 0.64f, 1.85f);
                Vector3 first = injection ? towerPort : ringPort;
                Vector3 firstControl = injection ? towerPort + outward * 1.5f : ringPort + frame.Along * controlLength;
                Vector3 lastControl = injection ? ringPort - frame.Along * controlLength : towerPort + outward * 1.5f;
                Vector3 last = injection ? ringPort : towerPort;
                Vector3[] path = TransferCurve(first, firstControl, lastControl, last);
                armor.Sweep(path, towerUp, 0.15f);
                for (int marker = 0; marker < 4; marker++)
                {
                    int index = 2 + marker * 6;
                    Vector3 tangent = (path[index + 1] - path[index - 1]).normalized;
                    metal.Tube(path[index], tangent, towerUp, 0.145f, 0.20f, 0.13f, 24);
                    surfaces[(int)Surface.BeamGuide0 + marker % 3].Tube(path[index], tangent, towerUp,
                        0.20f, 0.215f, 0.032f, 24);
                }
            }
        }

        private static float MountingDepth(OrbitalTrackIntersection.TowerSurface towerSurface, float height,
            Vector3 outward, Vector3 lateralOffset, float saddleRadius)
        {
            float nativeDepth = towerSurface.Radius(height, outward, lateralOffset) + 0.20f;
            // Ports share the generated saddle surface, whose radius can exceed the native tower mesh.
            float saddleDepth = Mathf.Sqrt(Mathf.Max(0, saddleRadius * saddleRadius - lateralOffset.sqrMagnitude));
            return Mathf.Max(nativeDepth, saddleDepth);
        }

        private static Vector3[] TransferCurve(Vector3 first, Vector3 firstControl, Vector3 lastControl, Vector3 last)
        {
            var points = new Vector3[25];
            for (int index = 0; index < points.Length; index++)
            {
                float amount = (float)index / (points.Length - 1);
                float remaining = 1 - amount;
                Vector3 point = first * (remaining * remaining * remaining)
                    + firstControl * (3 * remaining * remaining * amount);
                point += lastControl * (3 * remaining * amount * amount);
                points[index] = point + last * (amount * amount * amount);
            }

            return points;
        }

        private static bool TryFrame(PlanetFactory factory, int ring, int band, float longitude,
            int itemId, int entityId, float envelopeRadius, out BayFrame frame)
        {
            Vector3 anchor = OrbitalTextureLayout.TrackCenter(factory, ring, band, longitude, itemId);
            Vector3 up = anchor.normalized;
            Vector3 along = Vector3.Cross(Vector3.up, up).normalized;
            Vector3 across = Vector3.Cross(along, up).normalized;
            if (!OrbitalTrackIntersection.TryMeasure(factory, entityId, anchor, along, across, up,
                envelopeRadius, out Vector2 interval))
            {
                frame = default(BayFrame);
                return false;
            }

            float radius = new Vector2(anchor.x, anchor.z).magnitude;
            Vector3 first = OrbitalTextureLayout.TrackCenter(factory, ring, band,
                longitude + (interval.x - 0.4f) / radius, itemId);
            Vector3 last = OrbitalTextureLayout.TrackCenter(factory, ring, band,
                longitude + (interval.y + 0.4f) / radius, itemId);
            frame = new BayFrame(first, last, factory.entityPool[entityId].pos);
            return true;
        }
    }
}

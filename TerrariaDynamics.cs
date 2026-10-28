using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Box2D.NET;
using Terraria.ModLoader;

namespace TerrariaDynamics
{
	// Please read https://github.com/tModLoader/tModLoader/wiki/Basic-tModLoader-Modding-Guide#mod-skeleton-contents for more information about the various files in a mod.
	public class TerrariaDynamics : Mod
	{
		public override void Load()
		{
			B2Version version = B2Cores.b2GetVersion();
			Logger.Info($"Loaded Box2D.NET (assembly {typeof(B2Cores).Assembly.GetName().Version}, engine {version.major}.{version.minor}.{version.revision}).");
		}

		public override void HandlePacket(System.IO.BinaryReader reader, int whoAmI) =>
			Common.TileYards.YardSystem.Instance.HandlePacket(reader, whoAmI);
	}
}

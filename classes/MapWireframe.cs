﻿/*
 * YOGEME.exe, All-in-one Mission Editor for the X-wing series, XW through XWA
 * Copyright (C) 2007-2020 Michael Gaisser (mjgaisser@gmail.com)
 * This file authored by "JB" (Random Starfighter) (randomstarfighter@gmail.com)
 * Licensed under the MPL v2.0 or later
 * 
 * VERSION: 1.6.6+
 */

/* CHANGELOG
* v1.7, XXXXXX
* [NEW] created [JB]
*/

using System;
using System.IO;
using System.Collections.Generic;

/* Special thanks to:
 * Jérémy Ansel for documentation of the OPT format https://github.com/JeremyAnsel/XwaOptEditor
 * Rob for documentation of the CRFT, CPLX and SHIP formats used by XW93, XW94, and TIE DOS.
 */

/* [JB] I am not familiar with 3D math so there may potentially be errors or inefficiencies in the wireframe
 * implementation here. My prototyping code was originally written in C++ and ported here. I hope the result
 * is adequate.
 * 
 * WireframeManager is the central class that's exposed to the map form. Everything is abstracted away from the
 * map, so it can request a wireframe for a craft type and flight group, update it depending on orientation and
 * zoom, and render it on screen. All without the map needing to keep track of anything. Behind the scenes, the
 * Manager utilizes many other classes that load the wireframes, manipulate the data, and transform the result:
 *   OptFile: temporary container to parse and load OPT meshes.
 *   CraftFile: temporary container to parse and load CRFT, CPLX, or SHIP meshes.
 *   WireframeDefinition: converted from a loaded OPT or Craft for a particular craft type, organized into layers.
 *     MeshLayerDefinition: contains filtered vertices and line segments for a particular mesh type.
 *   WireframeInstance: a single instance of a craft wireframe to be drawn on the map.
 *     MeshLayerInstance: vertices are transformed into screen coordinates relative to the mesh origin.
 */

//TODO: overall, should add XML to just about everything, change fields to auto-properties, etc. Also, clear up Vertex/Vector, since it looks like they're not named properly
namespace Idmr.Yogeme
{
	public class Vector3
	{
		public float X;
		public float Y;
		public float Z;
		public Vector3()
		{
			X = 0.0f;
			Y = 0.0f;
			Z = 0.0f;
		}
		public Vector3(int x, int y, int z)
		{
			X = x;
			Y = y;
			Z = z;
		}
		public Vector3(float x, float y, float z)
		{
			X = x;
			Y = y;
			Z = z;
		}
		public Vector3(Vector3 other)
		{
			X = other.X;
			Y = other.Y;
			Z = other.Z;
		}
		public void MultTranspose(Matrix3 mat)
		{
			float vx = X;
			float vy = Y;
			float vz = Z;
			X = (float)(mat.V11 * vx + mat.V21 * vy + mat.V31 * vz);
			Y = (float)(mat.V12 * vx + mat.V22 * vy + mat.V32 * vz);
			Z = (float)(mat.V13 * vx + mat.V23 * vy + mat.V33 * vz);
		}
	}
	public class Matrix3
	{
		public double V11;
		public double V12;
		public double V13;
		public double V21;
		public double V22;
		public double V23;
		public double V31;
		public double V32;
		public double V33;

		/// <summary>Initializes a matrix ready for the needed rotational transform.</summary>
		/// <remarks>This is the matrix multiplication for the equations Roll * Yaw, in that order. When applying "Roll" to the vertices,<br/>
		/// the visible effect is pitch relative to the body. Yaw works as expected. Perhaps this is because the Z axis in-game is<br/>
		/// elevation, not depth?</remarks>
		public Matrix3(double yaw, double pitch)
		{
			V11 = Math.Cos(yaw);
			V12 = -Math.Sin(yaw);
			V13 = 0;
			V21 = (Math.Cos(pitch) * Math.Sin(yaw));
			V22 = (Math.Cos(pitch) * Math.Cos(yaw));
			V23 = -Math.Sin(pitch);
			V31 = (Math.Sin(pitch) * Math.Sin(yaw));
			V32 = (Math.Sin(pitch) * Math.Cos(yaw));
			V33 = Math.Cos(pitch);
		}
	}

	/// <summary>All nodes within the tree structure have a type that determines its data format.</summary>
	public enum OptNodeType
	{
		NullNode = -1,
		NodeGroup = 0,
		FaceData = 1,
		MeshVertices = 3,
		NodeReference = 7,
		VertexNormals = 11,
		TextureCoordinates = 13,
		Texture = 20,
		FaceGrouping = 21,	// aka Mesh Data
		Hardpoint = 22,
		RotationScale = 23,
		NodeSwitch = 24,
		MeshDescriptor = 25,
		TextureAlpha = 26,
		EngineGlow = 28,
	}

	/// <summary>All possible mesh types for each component in the model.</summary>
	/// <remarks>Used in the OPT and SHIP formats. Currently unknown if they're present in the older CRFT or CPLX formats.</remarks>
	public enum MeshType
	{
		Default = 0,
		MainHull,
		Wing,
		Fuselage,
		GunTurret,
		SmallGun,
		Engine,
		Bridge,
		ShieldGenerator,
		EnergyGenerator,
		Launcher,
		CommunicationSystem,
		BeamSystem,
		CommandSystem,
		DockingPlatform,
		LandingPlatform,
		Hangar,
		CargoPod,
		MiscHull,
		Antenna,
		RotaryWing,
		RotaryGunTurret,
		RotaryLauncher,
		RotaryCommunicationSystem,
		RotaryBeamSystem,
		RotaryCommandSystem,
		Hatch,
		Custom,
		WeaponSystem1,
		WeaponSystem2,
		PowerRegenerator,
		Reactor
	}

	/// <summary>Exposes some useful defaults and functions to assist program configuration for <see cref="MeshType"/> visibility.</summary>
	public static class MeshTypeHelper
	{
		// These predefined arrays help initialize the user's configuration as well as offering quick toggles to include or exclude a selection of visible mesh types.
		public static MeshType[] DefaultMeshes = new MeshType[] { MeshType.Default, MeshType.MainHull, MeshType.Wing, MeshType.Fuselage, MeshType.Bridge, MeshType.DockingPlatform, MeshType.LandingPlatform, MeshType.Hangar, MeshType.CargoPod, MeshType.MiscHull, MeshType.Engine, MeshType.RotaryWing, MeshType.Launcher };
		public static MeshType[] HullMeshes = new MeshType[] { MeshType.Default, MeshType.MainHull, MeshType.Wing, MeshType.Fuselage, MeshType.Engine, MeshType.Bridge, MeshType.Launcher, MeshType.MiscHull, MeshType.RotaryWing };
		public static MeshType[] MiscMeshes = new MeshType[] { MeshType.ShieldGenerator, MeshType.EnergyGenerator, MeshType.CommunicationSystem, MeshType.BeamSystem, MeshType.CommandSystem, MeshType.CargoPod, MeshType.Antenna, MeshType.RotaryCommunicationSystem, MeshType.RotaryBeamSystem, MeshType.RotaryCommandSystem, MeshType.Hatch, MeshType.Custom, MeshType.PowerRegenerator, MeshType.Reactor };
		public static MeshType[] WeaponMeshes = new MeshType[] { MeshType.GunTurret, MeshType.SmallGun, MeshType.RotaryGunTurret, MeshType.RotaryLauncher, MeshType.WeaponSystem1, MeshType.WeaponSystem2 };
		public static MeshType[] HangarMeshes = new MeshType[] { MeshType.DockingPlatform, MeshType.LandingPlatform, MeshType.Hangar };

		/// <summary>Returns the default MeshTypes combined into a single value.</summary>
		public static long GetDefaultFlags()
		{
			return GetFlags(DefaultMeshes);
		}

		/// <summary>Combines an array of enum-based MeshTypes into a single value.</summary>
		public static long GetFlags(MeshType[] list)
		{
			long retval = 0;
			foreach (MeshType value in list)
				retval |= (long)(1 << (int)value);
			return retval;
		}

		/// <summary>Combines an int array into a single value.</summary>
		public static long GetFlags(int[] list)
		{
			long retval = 0;
			foreach (int value in list)
				retval |= (long)(1 << value);
			return retval;
		}
	}

	/// <summary>Container to store the vertices of a single polygon face, which may have 3 or 4 vertices.</summary>
	public struct OptFace
	{
		public int[] VertexIndex;

		public OptFace(int v1, int v2, int v3, int v4)
		{
			VertexIndex = new int[4];
			VertexIndex[0] = v1;
			VertexIndex[1] = v2;
			VertexIndex[2] = v3;
			VertexIndex[3] = v4;
		}
	}

	/// <summary>Container to store all mesh faces of a single LOD.</summary>
	/// <remarks>Discarded prototyping code attempted to select a lower detail mesh to improve drawing performance, but it wasn't very helpful for normal models.</remarks>
	public class OptLod
	{
		public float Distance;
		public List<OptFace> Faces = new List<OptFace>();

		public OptLod()
		{
			Distance = float.MaxValue;
		}
		public OptLod(float dist)
		{
			Distance = dist;
		}
	}

	/// <summary>Holds all loaded information for a single component, which is a top-level node in the OPT tree.</summary>
	public class OptComponent
	{
		public OptNodeType NodeType = 0;
		public MeshType MeshType = MeshType.Default;
		public int LoadingLodIndex = 0;
		public List<Vector3> Vertices = new List<Vector3>();
		public List<OptLod> Lods = new List<OptLod>();
	}

	/// <summary>The OPT format was introduced with XvT/BoP, continued in XWA, and retrofitted into XWING and TIE for the Windows versions.</summary>
	/// <remarks>The format is a tree of nodes, utilizing C-style pointers to navigate each node in the tree and access their data elements.</remarks>
	public class OptFile
	{
		private int _basePosition;  // The file contents begin with some meta data that isn't part of the actual model data. This will be the stream position where the real data begins.
		private int _globalOffset;  // The first piece of real data in the file is a pointer to itself. Since the entire file would be contiguous in memory, subtracting from any other pointer address gives us a relative offset into the file.
		public List<OptComponent> Components = new List<OptComponent>();

		/// <summary>Initializes and attempts to load the contents from file.</summary>
		/// <returns>Returns true if the file is loaded.</returns>
		public bool LoadFromFile(string filename)
		{
			try
			{
				if (!File.Exists(filename))
					return false;
				using (FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
				{
					using (BinaryReader br = new BinaryReader(fs))
					{
						int version = br.ReadInt32();
						if (version <= 0) fs.Position += 4;

						parseTopNodes(fs, br);
					}
				}
			}
			catch
			{
				return false;
			}
			return true;
		}

		/// <summary>Parses all top-level nodes.</summary>
		/// <remarks>Typically each top-level node is a single component of a particular MeshType. Its MeshType and mesh information will be defined somewhere in its child node tree.</remarks>
		private void parseTopNodes(FileStream fs, BinaryReader br)
		{
			_basePosition = (int)fs.Position;
			_globalOffset = br.ReadInt32();
			fs.Position += 2;  // Skip Int16 heap index.
			int nodeCount = br.ReadInt32();
			int nodeTableOffset = br.ReadInt32();

			if (nodeTableOffset != 0)
				nodeTableOffset -= _globalOffset;

			Components = new List<OptComponent>();
			for (int i = 0; i < nodeCount; i++)
			{
				fs.Position = _basePosition + nodeTableOffset + (i * 4);
				int nodeOffset = br.ReadInt32();
				if (nodeOffset != 0)
				{
					nodeOffset -= _globalOffset;
					OptComponent comp = new OptComponent();
					fs.Position = _basePosition + nodeOffset;
					parseChildNodes(fs, br, comp);
					Components.Add(comp);
				}
			}
		}

		/// <summary>Recursively parses all child nodes of a top-level node.</summary>
		/// <remarks>Loads any relevant data into the specified component object.</remarks>
		private void parseChildNodes(FileStream fs, BinaryReader br, OptComponent node)
		{
			//int nameOffset = br.ReadInt32();
			fs.Position += 4;
			OptNodeType nodeType = (OptNodeType)br.ReadInt32();
			int childNodeCount = br.ReadInt32();
			int childNodeOffset = br.ReadInt32();
			int dataCount = br.ReadInt32();
			int dataOffset = br.ReadInt32();
			if (childNodeOffset != 0)
				childNodeOffset -= _globalOffset;

			node.NodeType = nodeType;
			if (dataOffset != 0)
				dataOffset -= _globalOffset;

			switch (nodeType)
			{
				case OptNodeType.MeshVertices:
					if (dataOffset == 0)
						break;
					fs.Position = _basePosition + dataOffset;
					for (int i = 0; i < dataCount; i++)
					{
						float x = br.ReadSingle();
						float y = br.ReadSingle();
						float z = br.ReadSingle();
						node.Vertices.Add(new Vector3(x, y, z));
					}
					break;
				case OptNodeType.FaceData:
					if (dataOffset == 0)
						break;
					if (node.LoadingLodIndex >= node.Lods.Count)
						break;
					fs.Position = _basePosition + dataOffset + 4;  // Into the data, skipping Int32 edgeCount.
					for (int i = 0; i < dataCount; i++)
					{
						int v1 = br.ReadInt32();
						int v2 = br.ReadInt32();
						int v3 = br.ReadInt32();
						int v4 = br.ReadInt32();
						node.Lods[node.LoadingLodIndex].Faces.Add(new OptFace(v1, v2, v3, v4));
						fs.Position += 48;  // Advance to next face.
					}
					break;
				case OptNodeType.MeshDescriptor:
					if (dataOffset == 0)
						break;
					fs.Position = _basePosition + dataOffset;
					node.MeshType = (MeshType)br.ReadInt32();
					break;
				case OptNodeType.FaceGrouping:
					if (dataOffset == 0)
						break;
					fs.Position = _basePosition + dataOffset;
					for (int i = 0; i < dataCount; i++)
					{
						float distance = br.ReadSingle();
						node.Lods.Add(new OptLod(distance));
					}
					while (node.Lods.Count < childNodeCount)
					{
						node.Lods.Add(new OptLod());
					}
					for (int i = 0; i < childNodeCount; i++)
					{
						node.LoadingLodIndex = i;
						fs.Position = _basePosition + childNodeOffset + (i * 4);
						int nodeOffset = br.ReadInt32();
						if (nodeOffset != 0)
						{
							nodeOffset -= _globalOffset;
							fs.Position = _basePosition + nodeOffset;
							parseChildNodes(fs, br, node);
						}
					}
					break;
			}
			// We already had a special case for FaceGrouping.
			if (nodeType != OptNodeType.FaceGrouping)
			{
				for (int i = 0; i < childNodeCount; i++)
				{
					fs.Position = _basePosition + childNodeOffset + (i * 4);
					int nodeOffset = br.ReadInt32();
					if (nodeOffset != 0)
					{
						nodeOffset -= _globalOffset;
						fs.Position = _basePosition + nodeOffset;
						parseChildNodes(fs, br, node);
					}
				}
			}
		}
	}

	/// <summary>This Vector3 is needed for the DOS craft formats.</summary>
	/// <remarks>Presented as an array to make it easier to access its members.</remarks>
	public class Vector3_Int16
	{
		public short[] Data = new short[3];

		public Vector3_Int16()
		{
			Data[0] = 0;
			Data[1] = 0;
			Data[2] = 0;
		}
	}

	/// <summary>Two vertex indices that define a line.</summary>
	public class Line
	{
		public int V1;
		public int V2;

		public Line(int v1, int v2)
		{
			V1 = v1;
			V2 = v2;
		}
	}

	/// <summary>Container to store all mesh faces of a single LOD.</summary>
	public class CraftLod
	{
		public int Distance;
		public short FileOffset;
		public List<Vector3_Int16> Vertices = new List<Vector3_Int16>();
		public List<Line> Lines = new List<Line>();

		public CraftLod(int distance, short fileOffset)
		{
			Distance = distance;
			FileOffset = fileOffset;
		}
	}

	/// <summary>Holds all loaded information for a single component.</summary>
	public class CraftComponent
	{
		public MeshType MeshType = MeshType.Default;
		public List<CraftLod> Lods = new List<CraftLod>();
	}

	/// <summary>This facilitates loading of all DOS craft formats (CRFT, CPLX, and SHIP).</summary>
	/// <remarks>The format is similar to OPT in the sense that it contains a list of components, along with various pieces of data. These resources are typically packed into uncompressed LFD archives, which are automatically handled by the loading functions.</remarks>
	public class CraftFile
	{
		public List<CraftComponent> Components = new List<CraftComponent>();
		// TODO: duplication of Lfd processing vs LfdReader, replace in the long run
		private LfdCraftFormat _lfdCraftFormat = LfdCraftFormat.None;

		/// <summary>Initializes the object and attempts to load relevant data from a standalone file.</summary>
		/// <remarks>Requires the craft file format to already be known, as the context cannot be determined from the file contents.</remarks>
		public bool LoadFromFile(string speciesName, LfdCraftFormat craftFormat)
		{
			if (!File.Exists(speciesName) || craftFormat == LfdCraftFormat.None)
				return false;
			_lfdCraftFormat = craftFormat;
			try
			{
				using (FileStream fs = new FileStream(speciesName, FileMode.Open, FileAccess.Read))
				{
					using (BinaryReader br = new BinaryReader(fs))
					{
						short fileSize = br.ReadInt16();
						if (_lfdCraftFormat == LfdCraftFormat.CRFT || _lfdCraftFormat == LfdCraftFormat.CPLX)
							parseXwing(fs, br);
						else if (_lfdCraftFormat == LfdCraftFormat.SHIP)
							parseTie(fs, br);
					}
				}
			}
			catch (Exception)
			{
				return false;
			}
			return true;
		}

		/// <summary>Initializes the object and attempts to load relevant data from within an LFD archive.</summary>
		public bool LoadFromArchive(string archiveName, string speciesName)
		{
			_lfdCraftFormat = LfdCraftFormat.None;
			if (!File.Exists(archiveName))
				return false;
			try
			{
				using (FileStream fs = new FileStream(archiveName, FileMode.Open, FileAccess.Read))
				{
					using (BinaryReader br = new BinaryReader(fs, System.Text.Encoding.GetEncoding(437)))	// IBM437
					{
						List<LfdResourceInfo> resourceList = loadLfdResourceTableFromStream(br);
						int offset = 0;

						LfdResourceInfo entry = getResourceInfo(resourceList, speciesName, out offset);
						if (entry != null)
						{
							_lfdCraftFormat = entry.GetCraftFormat();
							fs.Position = offset;

							LfdResourceInfo current = new LfdResourceInfo();
							current.ReadFromStream(br);
							if (current.Length != entry.Length)
								return false;

							short fileSize = br.ReadInt16();
							if (fileSize != current.Length - 2)
								return false;

							if (_lfdCraftFormat == LfdCraftFormat.CRFT || _lfdCraftFormat == LfdCraftFormat.CPLX)
								parseXwing(fs, br);
							else if (_lfdCraftFormat == LfdCraftFormat.SHIP)
								parseTie(fs, br);
						}
					}
				}
			}
			catch (Exception)
			{
				return false;
			}
			return true;
		}

		/// <summary>Returns a list of resources contained in an LFD archive.</summary>
		private List<LfdResourceInfo> loadLfdResourceTableFromStream(BinaryReader br)
		{
			List<LfdResourceInfo> result = new List<LfdResourceInfo>();
			LfdResourceInfo header = new LfdResourceInfo();
			header.ReadFromStream(br);
			if (header.Type != "RMAP")
				return result;

			result.Add(header);
			for (int i = 0; i < header.Length / 16; i++)
			{
				LfdResourceInfo resource = new LfdResourceInfo();
				resource.ReadFromStream(br);
				result.Add(resource);
			}
			return result;
		}

		/// <summary>Retrieves a resource entry from the specified list and calculates its file position.</summary>
		/// <returns>Returns the matching resource entry, or null if not found. If found, its file offset is placed in the output parameter.</returns>
		private LfdResourceInfo getResourceInfo(List<LfdResourceInfo> resourceList, string resourceName, out int fileOffset)
		{
			int totalSize = 0;
			for (int i = 0; i < resourceList.Count; i++)
			{
				if (resourceList[i].GetCraftFormat() != LfdCraftFormat.None && string.Compare(resourceList[i].Name, resourceName, StringComparison.InvariantCultureIgnoreCase) == 0)
				{
					fileOffset = totalSize;
					return resourceList[i];
				}
				totalSize += 16 + resourceList[i].Length;
			}
			fileOffset = 0;
			return null;
		}

		/// <summary>Parses the header and top-level contents of the CRFT and CPLX formats used in XWING.</summary>
		private void parseXwing(FileStream fs, BinaryReader br)
		{
			byte componentCount = br.ReadByte();
			byte shadingRecordCount = br.ReadByte();

			fs.Position += 16 * shadingRecordCount;

			for (int i = 0; i < componentCount; i++)
			{
				long recStart = fs.Position;
				short nodeOffset = br.ReadInt16();
				if (nodeOffset != 0)
				{
					fs.Position = recStart + nodeOffset;
					CraftComponent comp = new CraftComponent();
					parseNode(fs, br, comp);
					Components.Add(comp);
					fs.Position = recStart + 2;
				}
			}
		}

		/// <summary>Parses the header and top-level contents of the SHIP format used in TIE.</summary>
		private void parseTie(FileStream fs, BinaryReader br)
		{
			fs.Position += 30;   // Skip unknown.

			byte componentCount = br.ReadByte();
			byte shadingSetCount = br.ReadByte();
			// Skip int16 unknown, then 6 bytes per shading set.
			fs.Position += 2 + (6 * shadingSetCount);

			for (int i = 0; i < componentCount; i++)
			{
				long recStart = fs.Position;
				short meshType = br.ReadInt16();
				fs.Position += 42;
				short lodOffset = br.ReadInt16();

				fs.Position = recStart + lodOffset;
#pragma warning disable IDE0017 // Simplify object initialization
				CraftComponent comp = new CraftComponent();
#pragma warning restore IDE0017 // Simplify object initialization
				comp.MeshType = (MeshType)meshType;
				parseNode(fs, br, comp);
				Components.Add(comp);
				fs.Position = recStart + 64;
			}
		}

		/// <summary>Parses the node mesh information for all DOS formats.</summary>
		private void parseNode(FileStream fs, BinaryReader br, CraftComponent node)
		{
			long nodeBasePosition = fs.Position;

			int distance;
			short offset;
			do
			{
				distance = br.ReadInt32();
				offset = br.ReadInt16();
				node.Lods.Add(new CraftLod(distance, offset));
			} while (distance != 0x7FFFFFFF);

			for (int i = 0; i < node.Lods.Count; i++)
			{
				fs.Position = nodeBasePosition + (i * 6) + node.Lods[i].FileOffset;

				//short header = br.ReadInt16();
				fs.Position += 2;
				byte vertexCount = br.ReadByte();
				//byte unknown = br.ReadByte();
				fs.Position++;
				byte shapeCount = br.ReadByte();
				fs.Position += shapeCount; // Skip over the shape colors.
				fs.Position += 12;         // Skip boundMin and boundMax (each are 6 bytes, Vector3_16bit).

				for (int j = 0; j < vertexCount; j++)
				{
					short test;
					Vector3_Int16 v = new Vector3_Int16();
					for (int k = 0; k < 3; k++)
					{
						v.Data[k] = br.ReadInt16();
						test = (short)((v.Data[k] & 0xFF00) >> 8);
						if (test == 0x7F)
						{
							test = (short)((v.Data[k] & 0xFF) >> 1);
							if (j - test >= 0 && j - test < node.Lods[i].Vertices.Count)
							{
								v.Data[k] = node.Lods[i].Vertices[j - test].Data[k];
							}
						}
					}
					node.Lods[i].Vertices.Add(v);
				}

				if (_lfdCraftFormat == LfdCraftFormat.CPLX || _lfdCraftFormat == LfdCraftFormat.SHIP)
					fs.Position += 6 * vertexCount;  // Skip vertex normals.

				long shapeBasePosition = fs.Position;
				short[] shapeOffsets = new short[shapeCount];
				// Read shape headers.
				for (int j = 0; j < shapeCount; j++)
				{
					fs.Position += 6;  // Skip face normal.
					shapeOffsets[j] = br.ReadInt16();
				}

				for (int j = 0; j < shapeCount; j++)
				{
					fs.Position = shapeBasePosition + (j * 8) + shapeOffsets[j];
					byte type = br.ReadByte();
					byte vertices = (byte)(type & 0x0F);
					if (vertices == 2)
					{
						byte[] data = br.ReadBytes(7);
						node.Lods[i].Lines.Add(new Line(data[2], data[3]));
					}
					else
					{
						int length = 3 + (vertices * 2);
						byte[] data = br.ReadBytes(length);
						for (int k = 0; k < vertices; k++)
						{
							int v1 = data[k * 2];
							int v2 = data[(k + 1) * 2];
							node.Lods[i].Lines.Add(new Line(v1, v2));
						}
					}
				}
			}
		}
	}

	/// <summary>Stores a compiled list of vertices and lines derived from the mesh and its faces.</summary>
	/// <remarks>Multiple components of the same MeshType will added into the same layer.</remarks>
	public class MeshLayerDefinition
	{
		public MeshType MeshType;
		public List<Vector3> Vertices;
		public List<Line> Lines;

		public MeshLayerDefinition(MeshType createMeshType)
		{
			MeshType = createMeshType;
			Vertices = new List<Vector3>();
			Lines = new List<Line>();
		}
	}

	/// <summary>A finalized wireframe definition that is ready for use in the map.</summary>
	/// <remarks>It can be generated from an OptFile or CraftFile.</remarks>
	public class WireframeDefinition
	{
		/// <summary>Span of the widest dimension, derived from bounding box, expressed in raw units (40960 units = 1 km)</summary>
		public int LongestSpanRaw = 0;
		/// <summary>Span of the widest dimension, derived from bounding box, expressed in meters.</summary>
		public int LongestSpanMeters = 0;
		public List<MeshLayerDefinition> MeshLayerDefinitions = new List<MeshLayerDefinition>();

		/// <summary>Creates a definition from a loaded OPT.</summary>
		/// <remarks>Performs some basic optimization to prevent shared edges, so that lines don't have to be drawn twice.</remarks>
		public WireframeDefinition(OptFile opt)
		{
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			MeshLayerDefinition layer = getOrCreateMeshLayerDefinition(MeshType.MainHull);  // Create a default entry so that it's first in the list, for drawing purposes.
#pragma warning restore IDE0059 // Unnecessary assignment of a value

			foreach (OptComponent comp in opt.Components)
			{
				if (comp.Lods.Count == 0)
					continue;

				int[] vertUsed = new int[comp.Vertices.Count];
				HashSet<int> lineUsed = new HashSet<int>();

				layer = getOrCreateMeshLayerDefinition(comp.MeshType);
				foreach (OptFace face in comp.Lods[0].Faces)
				{
					for (int i = 0; i < 4; i++)
					{
						int vi = face.VertexIndex[i];
						// If the face is a triangle (rather than a quad), the last index will be -1.
						if (vi == -1)
							continue;
						if (vertUsed[vi] == 0)
						{
							layer.Vertices.Add(comp.Vertices[vi]);
							vertUsed[vi] = layer.Vertices.Count;  // One-based value.
						}
					}
					for (int i = 0; i < 3; i++)
					{
						int v1 = face.VertexIndex[i];
						int v2 = face.VertexIndex[i + 1];
						if (v1 == -1)
							continue;
						// For a triangle, the last vertex is missing. Link back to the first vertex in the face.
						if (v2 == -1)
							v2 = face.VertexIndex[0];

						// Normalize and construct a key to determine if this line already exists. Add if it doesn't.
						if (v2 < v1)
						{
							int temp = v1;
							v1 = v2;
							v2 = temp;
						}
						int key = v1 | (v2 << 16);
						if (!lineUsed.Contains(key))
						{
							v1 = vertUsed[v1] - 1; // Convert from one-based back to zero-based.
							v2 = vertUsed[v2] - 1;
							layer.Lines.Add(new Line(v1, v2));
							lineUsed.Add(key);
						}
					}
					// If the face is quadrilateral, we haven't linked the first and last vertices.
					// Perform the same thing as above.
					if (face.VertexIndex[0] >= 0 && face.VertexIndex[3] >= 0)
					{
						int v1 = face.VertexIndex[0];
						int v2 = face.VertexIndex[3];
						if (v2 < v1)
						{
							int temp = v1;
							v1 = v2;
							v2 = temp;
						}
						int key = v1 | (v2 << 16);
						if (!lineUsed.Contains(key))
						{
							v1 = vertUsed[v1] - 1; // Convert from one-based back to zero-based.
							v2 = vertUsed[v2] - 1;
							layer.Lines.Add(new Line(v1, v2));
							lineUsed.Add(key);
						}
					}
				}
			}
			calculateSize();
		}

		/// <summary>Creates a definition from a loaded DOS craft format (CRFT, CPLX, SHIP).</summary>
		/// <remarks>Performs some basic optimization to prevent shared edges, so that lines don't have to be drawn twice.</remarks>
		public WireframeDefinition(CraftFile craft)
		{
			// This function is conceptually similar to creating from OPT, except we already have our lines and don't need to examine the faces.
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			MeshLayerDefinition layer = getOrCreateMeshLayerDefinition(MeshType.MainHull);
#pragma warning restore IDE0059 // Unnecessary assignment of a value
			for (int i = 0; i < craft.Components.Count; i++)
			{
				CraftComponent comp = craft.Components[i];
				if (comp.Lods.Count == 0)
					continue;
				CraftLod lod = comp.Lods[0];

				int[] vertUsed = new int[lod.Vertices.Count];
				HashSet<int> lineUsed = new HashSet<int>();

				layer = getOrCreateMeshLayerDefinition(comp.MeshType);
				for (int j = 0; j < lod.Lines.Count; j++)
				{
					int v1 = lod.Lines[j].V1;
					int v2 = lod.Lines[j].V2;

					if (vertUsed[v1] == 0)
					{
						int x = lod.Vertices[v1].Data[0];
						int y = lod.Vertices[v1].Data[1];
						int z = lod.Vertices[v1].Data[2];
						layer.Vertices.Add(new Vector3(x, y, z));
						vertUsed[v1] = layer.Vertices.Count; // One-based.
					}
					if (vertUsed[v2] == 0)
					{
						int x = lod.Vertices[v2].Data[0];
						int y = lod.Vertices[v2].Data[1];
						int z = lod.Vertices[v2].Data[2];
						layer.Vertices.Add(new Vector3(x, y, z));
						vertUsed[v2] = layer.Vertices.Count;
					}

					// Normalize and construct a key to determine if this line already exists. Add if it doesn't.
					if (v2 < v1)
					{
						int temp = v1;
						v1 = v2;
						v2 = temp;
					}
					int key = v1 | (v2 << 16);
					if (!lineUsed.Contains(key))
					{
						v1 = vertUsed[v1] - 1;  // Convert back to zero-based index.
						v2 = vertUsed[v2] - 1;
						layer.Lines.Add(new Line(v1, v2));
						lineUsed.Add(key);
					}
				}
			}
			calculateSize();
		}

		/// <summary>Applies a scale to all vertices.</summary>
		/// <remarks>Needed for DOS models. Most craft must be scaled by 0.5 to get their proper size. The largest ships need to be scaled by 2.0</remarks>
		public void Scale(float scale)
		{
			if (scale <= 0)
				return;
			LongestSpanRaw = (int)(LongestSpanRaw * scale);
			LongestSpanMeters = (int)(LongestSpanMeters * scale);
			foreach (MeshLayerDefinition layer in MeshLayerDefinitions)
			{
				foreach (Vector3 vert in layer.Vertices)
				{
					vert.X *= scale;
					vert.Y *= scale;
					vert.Z *= scale;
				}
			}
		}

		/// <summary>Scans all vertices in the entire wireframe to determine its largest axis span based on its bounding box.</summary>
		private void calculateSize()
		{
			int vcount = 0;
			foreach (MeshLayerDefinition layer in MeshLayerDefinitions)
			{
				vcount += layer.Vertices.Count;
			}
			if (vcount == 0) // The model wasn't loaded.
			{
				LongestSpanRaw = 0;
				LongestSpanMeters = 0;
				return;
			}
			float minX = float.MaxValue;
			float maxX = float.MinValue;
			float minY = float.MaxValue;
			float maxY = float.MinValue;
			float minZ = float.MaxValue;
			float maxZ = float.MinValue;
			foreach (MeshLayerDefinition layer in MeshLayerDefinitions)
			{
				foreach (Vector3 v in layer.Vertices)
				{
					if (v.X < minX) minX = v.X;
					if (v.X > maxX) maxX = v.X;
					if (v.Y < minY) minY = v.Y;
					if (v.Y > maxY) maxY = v.Y;
					if (v.Z < minZ) minZ = v.Z;
					if (v.Z > maxZ) maxZ = v.Z;
				}
			}
			int spanX = (int)(maxX - minX);
			int spanY = (int)(maxY - minY);
			int spanZ = (int)(maxZ - minZ);
			LongestSpanRaw = spanX;
			if (spanY > LongestSpanRaw) LongestSpanRaw = spanY;
			if (spanZ > LongestSpanRaw) LongestSpanRaw = spanZ;
			LongestSpanMeters = (int)(LongestSpanRaw / 40.96);
		}

		/// <summary>Retrieves a layer for a particular MeshType. Creates an empty layer if it doesn't exist.</summary>
		private MeshLayerDefinition getOrCreateMeshLayerDefinition(MeshType meshType)
		{
			foreach (MeshLayerDefinition layer in MeshLayerDefinitions)
			{
				if (layer.MeshType == meshType)
					return layer;
			}
			MeshLayerDefinition entry = new MeshLayerDefinition(meshType);
			MeshLayerDefinitions.Add(entry);
			return entry;
		}
	}

	/// <summary>Built from a layer definition, this stores a cloned copy of the vertices that can be transformed without altering the definition.</summary>
	public class MeshLayerInstance
	{
		public MeshLayerDefinition MeshLayerDefinition = null;
		public List<Vector3> Vertices = null;

		public MeshLayerInstance(MeshLayerDefinition def)
		{
			MeshLayerDefinition = def;
			Vertices = new List<Vector3>();
			if (def != null)
			{
				Vertices.Capacity = def.Vertices.Count;
				for (int i = 0; i < def.Vertices.Count; i++)
					Vertices.Add(new Vector3(def.Vertices[i]));
			}
		}
		public bool MatchMeshFilter(long meshVisibilityFilter)
		{
			return (MeshLayerDefinition != null && (meshVisibilityFilter & (1 << (int)MeshLayerDefinition.MeshType)) != 0);
		}
	}

	/// <summary>Stores a local instance of a wireframe for a single craft/flightgroup.</summary>
	public class WireframeInstance
	{
		public List<MeshLayerInstance> LayerInstances = new List<MeshLayerInstance>();
		public WireframeDefinition ModelDef = null;

		// These two variables are used for cache purposes to determine if the model needs to be reloaded.
		public int AssignedCraftType;
		public int AssignedFGIndex;

		private bool _rebuildRequired = true;
		private int _curX = 0;
		private int _curY = 0;
		private int _curZ = 0;
		private int _dstX = 0;
		private int _dstY = 0;
		private int _dstZ = 0;
		private int _curZoom = 0;
		private MapForm.Orientation _curOrientation;
		private long _curVisibilityFlags = 0;
		private double _scaleMult;

		/// <summary>Creates a new instance from the specified definition.</summary>
		/// <remarks>The craftType and fgIndex parameters are used to identify this instance so that the underlying manager can change the model when necessary.</remarks>
		public WireframeInstance(WireframeDefinition def, int craftType, int fgIndex)
		{
			AssignedCraftType = craftType;
			AssignedFGIndex = fgIndex;
			ModelDef = def;
			if (def == null)
				return;

			LayerInstances = new List<MeshLayerInstance>();
			foreach (MeshLayerDefinition layer in ModelDef.MeshLayerDefinitions)
			{
				LayerInstances.Add(new MeshLayerInstance(layer));
			}
			// Trigger a refresh the next time it's updated.
			_rebuildRequired = true;
		}

		/// <summary>Determine whether the core instance has changed, and rebuild if needed.</summary>
		public void CheckAssignment(int craftType, int fgIndex)
		{
			if (craftType != AssignedCraftType || AssignedFGIndex != fgIndex)
			{
				AssignedCraftType = craftType;
				AssignedFGIndex = fgIndex;
				_rebuildRequired = true;
			}
		}

		/// <summary>Updates the transformed vertices as it should appear on screen, according to several parameters.</summary>
		/// <remarks>If no change is detected, the wireframe remains as is. Resulting vertex positions are relative to the model origin.</remarks>
		public void UpdateParams(Platform.BaseFlightGroup.BaseWaypoint cur, Platform.BaseFlightGroup.BaseWaypoint dest, int zoom, MapForm.Orientation orientation, long meshTypeVisibilityFlags)
		{
			if (ModelDef == null)
				return;
			if (!_rebuildRequired && _curX == cur.RawX && _curY == cur.RawY && _curZ == cur.RawZ && _dstX == dest.RawX && _dstY == dest.RawY && _dstZ == dest.RawZ && _curZoom == zoom && _curOrientation == orientation && _curVisibilityFlags == meshTypeVisibilityFlags)
				return;
			_rebuildRequired = false;
			_curX = cur.RawX;
			_curY = cur.RawY;
			_curZ = cur.RawZ;
			_dstX = dest.RawX;
			_dstY = dest.RawY;
			_dstZ = dest.RawZ;
			_curOrientation = orientation;
			_curZoom = zoom;
			_curVisibilityFlags = meshTypeVisibilityFlags;

			// Zoom is in pixels per KM. Model units are at a scale of 40960 units per KM.
			_scaleMult = (double)_curZoom / 40960.0;
			int diffX = _dstX - _curX;
			int diffY = _dstY - _curY;
			int diffZ = _dstZ - _curZ;

			double yaw = 0.0;
			double pitch = 0.0;
			if (_curX == _dstX && _curY == _dstY && _curZ == _dstZ)
			{
				if (dest.Enabled)
				{
					yaw = -Math.PI / 4;  // 45 degree turn clockwise and pitch up.
					pitch = (_curOrientation != MapForm.Orientation.XY ? -Math.PI / 4 : Math.PI / 4);
				}
			}
			else if (dest.Enabled)
			{
				if (_curOrientation == MapForm.Orientation.XY)
				{
					yaw = Math.Atan2(_curY - _dstY, _curX - _dstX);
					yaw += (Math.PI / 2);
					pitch = -Math.Atan2(diffZ, Math.Sqrt(diffX * diffX + diffY * diffY));
				}
				else if (_curOrientation == MapForm.Orientation.XZ)
				{
					yaw = Math.Atan2(_dstZ - _curZ, _curX - _dstX);
					yaw += (Math.PI / 2);
					pitch = -Math.Atan2(diffZ, Math.Sqrt(diffX * diffX + diffY * diffY));
				}
				else if (_curOrientation == MapForm.Orientation.YZ)
				{
					yaw = -Math.Atan2(_dstX - _curX, _curY - _dstY);
					pitch = -Math.Atan2(diffZ, Math.Sqrt(diffX * diffX + diffY * diffY));
				}
				if (yaw > Math.PI)
					yaw -= Math.PI * 2;
			}

			updatePoints(_scaleMult, yaw, pitch);
		}

		/// <summary>Recalculates the positions of all vertices according to zoom and rotation.</summary>
		private void updatePoints(double scaleMult, double yaw, double pitch)
		{
			Matrix3 mat = new Matrix3(yaw, pitch);
			foreach (MeshLayerInstance cinst in LayerInstances)
			{
				if (!cinst.MatchMeshFilter(_curVisibilityFlags))
					continue;
				for (int i = 0; i < cinst.Vertices.Count; i++)
				{
					Vector3 v = cinst.Vertices[i];
					v.X = (float)(cinst.MeshLayerDefinition.Vertices[i].X * scaleMult);
					v.Y = (float)(cinst.MeshLayerDefinition.Vertices[i].Y * scaleMult);
					v.Z = (float)(-cinst.MeshLayerDefinition.Vertices[i].Z * scaleMult);  // Inverted so they appear properly. Maybe this could be handled during the load?
					v.MultTranspose(mat);
				}
			}
		}
	}

	/// <summary>Provides context to anything that needs to load a DOS model.</summary>
	public enum LfdCraftFormat
	{
		None = 0,
		CRFT = 1,
		CPLX = 2,
		SHIP = 3,
	}

	/// <summary>Stores the header information of a single resource entry within an LFD archive.</summary>
	public class LfdResourceInfo
	{
		public string Type;
		public string Name;
		public int Length;

		public LfdResourceInfo()
		{
			Type = "";
			Name = "";
			Length = 0;
		}

		public void ReadFromStream(BinaryReader br)
		{
			Type = new string(br.ReadChars(4)).Trim();
			Name = new string(br.ReadChars(8)).Trim();
			if (Type.IndexOf('\0') >= 0)
				Type = Type.Remove(Type.IndexOf('\0'));
			if (Name.IndexOf('\0') >= 0)
				Name = Name.Remove(Name.IndexOf('\0'));
			Length = br.ReadInt32();
		}
		public LfdCraftFormat GetCraftFormat()
		{
			if (Type == "CRFT") return LfdCraftFormat.CRFT;
			else if (Type == "CPLX") return LfdCraftFormat.CPLX;
			else if (Type == "SHIP") return LfdCraftFormat.SHIP;
			return LfdCraftFormat.None;
		}
	}

	/// <summary>The central class exposed to MapForm for accessing and managing wireframe models.</summary>
	/// <remarks>Most of the heavy-lifting is managed internally, abstracting as much work as possible away from the MapForm.</remarks>
	public class WireframeManager
	{
		Settings.Platform _curPlatform = Settings.Platform.None;   // The current loaded platform.
		string _curMissionPath = "";                               // The full path+filename of the current mission file. Used when attempting to auto-detect the game directory for available model files.
		string _curInstallPath = "";                               // The current installation path. Used to detect if the user has changed the config, and reload if necessary.
		string _modelLoadDirectory = "";                           // Contains the resolved path that all model resources are loaded from.

		Dictionary<int, WireframeDefinition> _wireframeDefinitions = null;    // Indexed by craftType, to store the loaded wireframe definition for that craft.
		List<WireframeInstance> _wireframeInstances = null;                   // Indexed by FlightGroup, so that each item in the map has its own instance.
		List<CraftData> _craftData = null;                                   // Indexed by craftType. Contains informational data loaded from an external file, most importantly the species resource names to load.

		// Only used for DOS formats.
		Dictionary<string, string> _dosSpeciesMap = null;          // Maps a list of all available species (as scanned from the SPECIES*.LFD archives) to the full path+filename of the archive it can be loaded from. (Ex: DREAD -> *path*\SPECIES2.LFD)
		LfdCraftFormat _dosCraftFormat = LfdCraftFormat.None;      // Required for X-wing, specifically one file (BWING.CRF). It exists as a standalone file, not archived in SPECIES.LFD. Since the format cannot be derived from the file extension (XW93 and XW94 have the same file name), the context must be determined from the assets within SPECIES.LFD.

		public WireframeManager()
		{
			_wireframeDefinitions = new Dictionary<int, WireframeDefinition>();
			_wireframeInstances = new List<WireframeInstance>();
		}

		/// <summary>Creates a WireframeInstance, or retrieves an existing one.</summary>
		/// <remarks>Automatically replaces the instance if the craftType or fgIndex has changed.</remarks>
		public WireframeInstance GetOrCreateWireframeInstance(int craftType, int fgIndex)
		{
			if (fgIndex < 0)
				return null;

			WireframeDefinition def = getWireframeDefinition(craftType);
			if (def == null)
				return null;

			// Pad the list so there's no out-of-bounds problems.
			while (_wireframeInstances.Count <= fgIndex)
			{
				_wireframeInstances.Add(null);
			}

			if (_wireframeInstances[fgIndex] == null)
				_wireframeInstances[fgIndex] = createWireframeInstance(def, craftType, fgIndex);
			else
				_wireframeInstances[fgIndex] = update(_wireframeInstances[fgIndex], craftType, fgIndex);

			return _wireframeInstances[fgIndex];
		}

		/// <summary>Prepares the manager to use a specific platform.</summary>
		/// <remarks>Handles basic tasks required when changing platforms, resetting the model cache and determining a new directory to load models from.</remarks>
		public void SetPlatform(Settings.Platform platform, Settings config)
		{
			// Retrieve the current installation path. If the platform remains the same, but the user has chosen a different folder, we'll be able to reload.
			string installPath = CraftDataManager.GetInstance().GetInstallPath();

			if (_curPlatform != platform || config.LastMission != _curMissionPath || _curInstallPath != installPath)
			{
				// Prepare new cache and reset the loading context.
				_wireframeDefinitions = new Dictionary<int, WireframeDefinition>();
				_wireframeInstances = new List<WireframeInstance>();
				_dosCraftFormat = LfdCraftFormat.None;
				_dosSpeciesMap = new Dictionary<string, string>();

				if (platform != Settings.Platform.None)
				{
					_modelLoadDirectory = CraftDataManager.GetInstance().GetModelPath();

					// Detect if DOS models exist and retrieve the information necessary to load them.
					string path = Path.Combine(_modelLoadDirectory, "species.lfd");
					if (File.Exists(path))
					{
						parseSpeciesFile(path);
						if (platform == Settings.Platform.TIE)
						{
							parseSpeciesFile(Path.Combine(_modelLoadDirectory, "species2.lfd"));
							parseSpeciesFile(Path.Combine(_modelLoadDirectory, "species3.lfd"));
						}
					}
				}
			}
			_curMissionPath = config.LastMission;
			_curInstallPath = installPath;
			_craftData = CraftDataManager.GetInstance().GetCraftDataList();
			_curPlatform = platform;
		}

		/// <summary>Opens a DOS SPECIES*.LFD archive, generating a list of resources it contains.</summary>
		/// <remarks>Also detects the craft file format to establish a proper loading context.</remarks>
		private void parseSpeciesFile(string filename)
		{
			if (!File.Exists(filename))
				return;
			try
			{
				using (FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
				{
					using (BinaryReader br = new BinaryReader(fs, System.Text.Encoding.GetEncoding(437)))
					{
						LfdResourceInfo resource = new LfdResourceInfo();
						resource.ReadFromStream(br);
						if (resource.Type == "RMAP")
						{
							int count = resource.Length / 16;
							for (int i = 0; i < count; i++)
							{
								resource.ReadFromStream(br);

								_dosSpeciesMap.Add(resource.Name.ToLower(), filename);
								if (_dosCraftFormat == LfdCraftFormat.None)
									_dosCraftFormat = resource.GetCraftFormat();
							}
						}
					}
				}
			}
			catch (Exception) { }
		}

		/// <summary>Loads a model definition into the cache, or retrieves an already existing cache entry.</summary>
		/// <remarks>If not already loaded, searches through the possible resource names to find a matching OPT or DOS species entry. If found, the mesh is loaded and converted to a ready format that the wireframe system can use.</remarks>
		/// <returns>Returns a model definition. If the model failed to load, the definition will be empty, but valid. Returns null if out of range.</returns>
		private WireframeDefinition getWireframeDefinition(int craftType)
		{
			// Check if already loaded.
			if (_wireframeDefinitions.ContainsKey(craftType))
				return _wireframeDefinitions[craftType];
			if (_craftData == null || craftType < 0 || craftType >= _craftData.Count)
				return null;

			string resourceNames = _craftData[craftType].ResourceNames.ToLower();
			WireframeDefinition def;
			if (_dosCraftFormat == LfdCraftFormat.None)
			{
				OptFile opt = new OptFile();
				string[] names = resourceNames.Split('|');
				foreach (string s in names)
				{
					string s2 = s;
					if (s2.IndexOf('*') >= 0)
						s2 = s2.Remove(s2.IndexOf('*'));
					if (_curPlatform == Settings.Platform.BoP)
					{
						if (opt.LoadFromFile(Path.Combine(_modelLoadDirectory, s2 + ".op1")))
							break;
					}
					if (opt.LoadFromFile(Path.Combine(_modelLoadDirectory, s2 + ".opt")))
						break;
				}
				def = new WireframeDefinition(opt);
			}
			else
			{
				CraftFile craft = new CraftFile();
				float scale = 0.5F;  // Default scale for most ships.
				string[] resNames = resourceNames.Split('|');
				foreach (string s in resNames)
				{
					string s2 = s;
					if (s2.IndexOf('*') >= 0)
					{
						float.TryParse(s2.Substring(s2.IndexOf('*') + 1), out scale);
						s2 = s2.Remove(s2.IndexOf('*'));
					}
					if (_dosSpeciesMap.ContainsKey(s2))
					{
						if (craft.LoadFromArchive(_dosSpeciesMap[s2], s2))
							break;
					}
					else
					{
						// This is really only required for the B-wing in the DOS versions of XWING, which exists in a standalone file.
						if (craft.LoadFromFile(Path.Combine(_modelLoadDirectory, s2 + ".cft"), _dosCraftFormat))
							break;
					}
				}
				def = new WireframeDefinition(craft);
				def.Scale(scale);
			}
			_wireframeDefinitions.Add(craftType, def);
			return def;
		}

		/// <summary>Creates a new WireframeInstance from a definition.</summary>
		private WireframeInstance createWireframeInstance(WireframeDefinition def, int craftType, int fgIndex)
		{
			if (def == null)
				return null;
			return new WireframeInstance(def, craftType, fgIndex);
		}

		/// <summary>Checks if an existing WireframeInstance needs to change its model type.</summary>
		/// <returns>Returns the current instance if nothing changed, otherwise returns a new instance.</returns>
		private WireframeInstance update(WireframeInstance currentInstance, int craftType, int fgIndex)
		{
			if (currentInstance == null)
				return null;

			WireframeInstance ret = currentInstance;
			if (craftType != currentInstance.AssignedCraftType)
			{
				WireframeDefinition def = getWireframeDefinition(craftType);
				ret = createWireframeInstance(def, craftType, fgIndex);
			}
			else
			{
				currentInstance.CheckAssignment(craftType, fgIndex);
			}
			return ret;
		}
	}
}
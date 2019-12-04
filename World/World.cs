using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using static Godot.Mathf;
using static SteelMath;


public class World : Node
{
	public const float DayNightMinutes = 30f;
	public const int PlatformSize = 12;
	public const int ChunkSize = 9*PlatformSize;

	public static Dictionary<Items.ID, PackedScene> Scenes = new Dictionary<Items.ID, PackedScene>();

	public static float Time { get; private set; } = 15f*DayNightMinutes;

	public static Dictionary<Tuple<int,int>, ChunkClass> Chunks = new Dictionary<Tuple<int,int>, ChunkClass>();
	public static Dictionary<int, List<Tuple<int,int>>> RemoteLoadedChunks = new Dictionary<int, List<Tuple<int,int>>>();
	public static Dictionary<int, int> ChunkLoadDistances = new Dictionary<int, int>();
	public static List<DroppedItem> ItemList = new List<DroppedItem>();
	public static GridClass Grid = new GridClass();
	public static AStar Pathfinder = new AStar();

	public static bool IsOpen = false;
	public static string SaveName = null;

	public static Node TilesRoot = null;
	public static Node EntitiesRoot = null;
	public static Node MobsRoot = null;
	public static ProceduralSky WorldSky = null;
	public static Godot.Environment WorldEnv = null;

	private static PackedScene DroppedItemScene;
	private static PackedScene DebugPlotPointScene;

	public static World Self;

	World()
	{
		if(Engine.EditorHint) {return;}

		Self = this;

		DroppedItemScene = GD.Load<PackedScene>("res://Items/DroppedItem.tscn");
		DebugPlotPointScene = GD.Load<PackedScene>("res://World/DebugPlotPoint.tscn");

		Directory TilesDir = new Directory();
		TilesDir.Open("res://World/Scenes/");
		TilesDir.ListDirBegin(true, true);
		string FileName = TilesDir.GetNext();
		while(true)
		{
			if(FileName == "")
			{
				break;
			}
			PackedScene Scene = GD.Load("res://World/Scenes/"+FileName) as PackedScene;
			if((Scene.Instance() as Tile) == null)
			{
				throw new System.Exception($"Tile scene '{FileName}' does not inherit Structure");
			}

			FileName = TilesDir.GetNext();
		}

		foreach(Items.ID Type in System.Enum.GetValues(typeof(Items.ID)))
		{
			File ToLoad = new File();
			if(ToLoad.FileExists("res://World/Scenes/" + Type.ToString() + ".tscn"))
			{
				Scenes.Add(Type, GD.Load("res://World/Scenes/" + Type.ToString() + ".tscn") as PackedScene);
			}
			else
			{
				Scenes.Add(Type, GD.Load("res://World/Scenes/ERROR.tscn") as PackedScene);
			}
		}
	}


	public override void _Ready()
	{
		WorldEnv = GetTree().Root.GetNode<WorldEnvironment>("RuntimeRoot/WorldEnvironment").Environment;
		WorldSky = WorldEnv.BackgroundSky as ProceduralSky;
	}


	public static void DebugPlot(Vector3 Position)
	{
		MeshInstance Point = DebugPlotPointScene.Instance() as MeshInstance;
		EntitiesRoot.AddChild(Point);
		Point.Translation = Position;
	}


	public static void DefaultPlatforms()
	{
		Place(Items.ID.PLATFORM, new Vector3(), new Vector3(), 0);

		for(int i = 0; i < 50; i++)
			Mobs.SpawnMob(Mobs.ID.Cat);
	}


	public static void Start()
	{
		Close();
		Menu.Close();

		Items.SetupItems();

		Node SkyScene = ((PackedScene)GD.Load("res://World/SkyScene.tscn")).Instance();
		SkyScene.Name = "SkyScene";
		Game.RuntimeRoot.AddChild(SkyScene);

		TilesRoot = new Node();
		TilesRoot.Name = "TilesRoot";
		SkyScene.AddChild(TilesRoot);

		EntitiesRoot = new Node();
		EntitiesRoot.Name = "EntitiesRoot";
		SkyScene.AddChild(EntitiesRoot);

		MobsRoot = new Node();
		MobsRoot.Name = "MobsRoot";
		SkyScene.AddChild(MobsRoot);

		Time = DayNightMinutes*60/4;
		IsOpen = true;
	}


	public static void Close()
	{
		if(Game.RuntimeRoot.HasNode("SkyScene"))
		{
			Game.RuntimeRoot.GetNode("SkyScene").Free();
			//Free instead of QueueFree to prevent crash when starting new world in same frame
		}

		//This is NOT a leak! Their parent was just freed ^
		TilesRoot = null;
		EntitiesRoot = null;
		MobsRoot = null;

		Net.Players.Clear();
		Game.PossessedPlayer = null;

		Pathfinder.Clear();
		Chunks.Clear();
		RemoteLoadedChunks.Clear();
		ItemList.Clear();
		Grid.Clear();

		Items.IdInfos.Clear();

		SaveName = null;
		IsOpen = false;
	}


	public static void Clear()
	{
		List<Tile> Branches = new List<Tile>();
		foreach(KeyValuePair<Tuple<int,int>, ChunkClass> Chunk in Chunks)
		{
			foreach(Tile Branch in Chunk.Value.Tiles)
			{
				Branches.Add(Branch);
			}
		}
		foreach(Tile Branch in Branches)
		{
			Branch.Remove(Force:true);
		}

		DroppedItem[] RemovingItems = new DroppedItem[ItemList.Count];
		ItemList.CopyTo(RemovingItems);
		foreach(DroppedItem Item in RemovingItems)
		{
			Item.Remove();
		}

		foreach(Node Entity in EntitiesRoot.GetChildren())
			Entity.QueueFree();

		Pathfinder.Clear();
		Chunks.Clear();
		Grid.Clear();

		foreach(KeyValuePair<int, List<Tuple<int,int>>> Pair in RemoteLoadedChunks)
		{
			RemoteLoadedChunks[Pair.Key].Clear();
		}
	}


	[Remote]
	public void RequestClear()
	{
		Clear();
	}


	public static void Save(string SaveNameArg)
	{
		Directory SaveDir = new Directory();
		if(SaveDir.DirExists($"user://Saves/{SaveNameArg}"))
		{
			System.IO.Directory.Delete($"{OS.GetUserDataDir()}/Saves/{SaveNameArg}", true);
		}

		int SaveCount = 0;
		foreach(KeyValuePair<System.Tuple<int, int>, ChunkClass> Chunk in Chunks)
		{
			SaveCount += SaveChunk(Chunk.Key, SaveNameArg);
		}
		Console.Log($"Saved {SaveCount.ToString()} structures to save '{SaveNameArg}'");
	}


	public static bool Load(string SaveNameArg)
	{
		if(string.IsNullOrEmpty(SaveNameArg) || string.IsNullOrWhiteSpace(SaveNameArg))
		{
			throw new Exception("Invalid save name passed to World.Save");
		}

		Directory SaveDir = new Directory();
		if(SaveDir.DirExists($"user://Saves/{SaveNameArg}"))
		{
			Clear();
			Net.SteelRpc(Self, nameof(RequestClear));
			DefaultPlatforms();

			bool ChunksDir = false;
			if(SaveDir.DirExists($"user://Saves/{SaveNameArg}/Chunks"))
			{
				SaveDir.Open($"user://Saves/{SaveNameArg}/Chunks");
				ChunksDir = true;
			}
			else
				SaveDir.Open($"user://Saves/{SaveNameArg}");
			SaveDir.ListDirBegin(true, true);

			int PlaceCount = 0;
			while(true)
			{
				string FileName = SaveDir.GetNext();
				if(FileName.Empty())
				{
					//Iterated through all files
					break;
				}

				string Path;
				if(ChunksDir)
					Path = $"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/Chunks/{FileName}";
				else
					Path = $"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/{FileName}";
				Tuple<bool,int> Returned = LoadChunk(System.IO.File.ReadAllText(Path));
				PlaceCount += Returned.Item2;
				if(!Returned.Item1)
				{
					Console.ThrowLog($"Invalid chunk file {FileName} loading save '{SaveNameArg}'");
				}
			}
			SaveName = SaveNameArg;
			Console.Log($"Loaded {PlaceCount.ToString()} structures from save '{SaveNameArg}'");
			return true;
		}
		else
		{
			SaveName = null;
			Console.ThrowLog($"Failed to load save '{SaveNameArg}' as it does not exist");
			return false;
		}
	}


	public static Vector3 GetChunkPos(Vector3 Position)
	{
		return new Vector3(Mathf.RoundToInt(Position.x/ChunkSize)*ChunkSize, 0, Mathf.RoundToInt(Position.z/ChunkSize)*ChunkSize);
	}


	public static Tuple<int,int> GetChunkTuple(Vector3 Position)
	{
		return new Tuple<int,int>(Mathf.RoundToInt(Position.x/ChunkSize)*ChunkSize, Mathf.RoundToInt(Position.z/ChunkSize)*ChunkSize);
	}


	static bool ChunkExists(Vector3 Position)
	{
		return ChunkExists(GetChunkTuple(Position));
	}


	static bool ChunkExists(Tuple<int, int> Position)
	{
		return Chunks.ContainsKey(Position);
	}


	static void AddTileToChunk(Tile Branch)
	{
		if(ChunkExists(Branch.Translation))
		{
			List<Tile> Chunk = Chunks[GetChunkTuple(Branch.Translation)].Tiles;
			Chunk.Add(Branch);
			Chunks[GetChunkTuple(Branch.Translation)].Tiles = Chunk;
		}
		else
		{
			ChunkClass Chunk = new ChunkClass();
			Chunk.Tiles = new List<Tile>{Branch};
			Chunks.Add(GetChunkTuple(Branch.Translation), Chunk);
		}
	}


	public static void AddItemToChunk(DroppedItem Item)
	{
		if(ChunkExists(Item.Translation))
		{
			List<DroppedItem> Items = Chunks[GetChunkTuple(Item.Translation)].Items;
			Items.Add(Item);
			Chunks[GetChunkTuple(Item.Translation)].Items = Items;
		}
		else
		{
			ChunkClass Chunk = new ChunkClass();
			Chunk.Items.Add(Item);
			Chunks.Add(GetChunkTuple(Item.Translation), Chunk);
		}
	}


	public static Tile PlaceOn(Items.ID BranchType, Tile Base, float PlayerOrientation, int BuildRotation, Vector3 HitPoint, int OwnerId)
	{
		Vector3? Position = Items.TryCalculateBuildPosition(BranchType, Base, PlayerOrientation, BuildRotation, HitPoint);
		if(Position != null) //If null then unsupported branch/base combination
		{
			Vector3 Rotation = Items.CalculateBuildRotation(BranchType, Base, PlayerOrientation, BuildRotation, HitPoint);
			return Place(BranchType, (Vector3)Position, Rotation, OwnerId);
		}

		return null;
	}


	public static Tile Place(Items.ID BranchType, Vector3 Position, Vector3 Rotation, int OwnerId)
	{
		string Name = System.Guid.NewGuid().ToString();
		Tile Branch = Self.PlaceWithName(BranchType, Position, Rotation, OwnerId, Name);

		if(Self.GetTree().NetworkPeer != null) //Don't sync place if network is not ready
		{
			Net.SteelRpc(Self, nameof(PlaceWithName), new object[] {BranchType, Position, Rotation, OwnerId, Name});
		}

		return Branch;
	}


	[Remote]
	public Tile PlaceWithName(Items.ID BranchType, Vector3 Position, Vector3 Rotation, int OwnerId, string Name)
	{
		Vector3 LevelPlayerPos = new Vector3(Game.PossessedPlayer.Translation.x,0,Game.PossessedPlayer.Translation.z);

		//Nested if to prevent very long line
		if(GetTree().NetworkPeer != null && !GetTree().IsNetworkServer())
		{
			if(GetChunkPos(Position).DistanceTo(LevelPlayerPos) > Game.ChunkRenderDistance*(PlatformSize*9))
			{
				//If network is inited, not the server, and platform it to far away then...
				return null; //...don't place
			}
		}

		Tile Branch = Scenes[BranchType].Instance() as Tile;
		Branch.Type = BranchType;
		Branch.OwnerId = OwnerId;
		Branch.Translation = Position;
		Branch.RotationDegrees = Rotation;
		Branch.Name = Name; //Name is a GUID and can be used to reference a structure over network
		TilesRoot.AddChild(Branch);

		AddTileToChunk(Branch);
		Grid.AddItem(Branch);
		Grid.QueueUpdateNearby(Branch.Translation);

		if(GetTree().NetworkPeer != null && GetTree().IsNetworkServer())
		{
			TryAddTileToPathfinder(Branch);

			if(GetChunkPos(Position).DistanceTo(LevelPlayerPos) > Game.ChunkRenderDistance*(PlatformSize*9))
			{
				//If network is inited, am the server, and platform is to far away then...
				Branch.Hide(); //...make it not visible but allow it to remain in the world
			}

			foreach(int Id in Net.PeerList)
			{
				if(Id == Net.ServerId) //Skip self (we are the server)
				{
					continue;
				}

				Vector3 PlayerPos = Net.Players[Id].Translation;
				if(GetChunkPos(Position).DistanceTo(new Vector3(PlayerPos.x, 0, PlayerPos.z)) <= ChunkLoadDistances[Id]*(PlatformSize*9))
				{
					if(!RemoteLoadedChunks[Id].Contains(GetChunkTuple(Position)))
					{
						RemoteLoadedChunks[Id].Add(GetChunkTuple(Position));
					}
				}
			}
		}

		return Branch;
	}


	public static void TryAddTileToPathfinder(Tile Branch) //This is a bit messy TODO: Improve this POS
	{
		switch(Branch.Type) //Filter out any tiles which should not be added to the pathfinder
		{
			case(Items.ID.PLATFORM):
			case(Items.ID.SLOPE):
				break;

			default:
				return;
		}

		Branch.PathId = Pathfinder.GetAvailablePointId();
		Pathfinder.AddPoint(Branch.PathId, Branch.Translation + new Vector3(0,2,0));

		bool ForwardAllowed = true;
		bool BackwardAllowed = true;
		bool RightAllowed = true;
		bool LeftAllowed = true;

		foreach(IInGrid Entry in Grid.GetItems(Branch.Translation)) //Set initial allowed state from walls blocking/not blocking
		{
			if(Entry is Tile FellowBranch)
			{
				if(FellowBranch.Type == Items.ID.WALL)
				{
					if(FellowBranch.Translation.z > Branch.Translation.z)
						ForwardAllowed = false;
					else if(FellowBranch.Translation.z < Branch.Translation.z)
						BackwardAllowed = false;
					else if(FellowBranch.Translation.x < Branch.Translation.x)
						RightAllowed = false;
					else if(FellowBranch.Translation.x > Branch.Translation.x)
						LeftAllowed = false;
				}

				else if(Branch.Type == Items.ID.PLATFORM && FellowBranch.Type == Items.ID.SLOPE)
				{
					switch(SnapToGrid(LoopRotation(FellowBranch.RotationDegrees.y), 360, 4))
					{
						case(0): {
							BackwardAllowed = false;
							break;
						}

						case(180): {
							ForwardAllowed = false;
							break;
						}

						case(270): {
							LeftAllowed = false;
							break;
						}

						case(90): {
							RightAllowed = false;
							break;
						}

						default:
							break;
					}
				}
			}
		}

		List<IInGrid> Forward = null;
		List<IInGrid> Backward = null;
		List<IInGrid> Right = null;
		List<IInGrid> Left = null;
		if(Branch.Type == Items.ID.PLATFORM)
		{
			Forward = Grid.GetItems(Branch.Translation + new Vector3(0, 0, PlatformSize));
			Backward = Grid.GetItems(Branch.Translation + new Vector3(0, 0, -PlatformSize));
			Right = Grid.GetItems(Branch.Translation + new Vector3(-PlatformSize, 0, 0));
			Left = Grid.GetItems(Branch.Translation + new Vector3(PlatformSize, 0, 0));
		}
		else if(Branch.Type == Items.ID.SLOPE)
		{
			switch(SnapToGrid(LoopRotation(Branch.RotationDegrees.y), 360, 4))
			{
				case(0): { //Forward
					Forward = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(0, PlatformSize, PlatformSize));
					Backward = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(0, 0, -PlatformSize))
						.Union(Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(0, -PlatformSize, -PlatformSize))
						       .Where(g => g is Tile t && t.Type == Items.ID.SLOPE))
						.ToList();

					RightAllowed = false;
					LeftAllowed = false;
					break;
				}

				case(180): { //Backward
					Forward = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(0, 0, PlatformSize))
						.Union(Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(0, -PlatformSize, PlatformSize))
						       .Where(g => g is Tile t && t.Type == Items.ID.SLOPE))
						.ToList();
					Backward = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(0, PlatformSize, -PlatformSize));

					RightAllowed = false;
					LeftAllowed = false;
					break;
				}

				case(270): { //Right
					Right = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(-PlatformSize, PlatformSize, 0));
					Left = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(PlatformSize, 0, 0))
						.Union(Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(PlatformSize, -PlatformSize, 0))
						       .Where(g => g is Tile t && t.Type == Items.ID.SLOPE))
						.ToList();

					ForwardAllowed = false;
					BackwardAllowed = false;
					break;
				}

				case(90): { //Left
					Right = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(-PlatformSize, 0, 0))
						.Union(Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(-PlatformSize, -PlatformSize, 0))
						       .Where(g => g is Tile t && t.Type == Items.ID.SLOPE))
						.ToList();
					Left = Grid.GetItems(GridClass.CalculateArea(Branch.Translation) + new Vector3(PlatformSize, PlatformSize, 0));

					ForwardAllowed = false;
					BackwardAllowed = false;
					break;
				}

				default:
					break;
			}
		}
		else
			throw new Exception($"No code exists to build Forward/Backward/Left/Right lists for a tile of type {Branch.Type}");


		if(Branch.Type == Items.ID.PLATFORM)
		{
			if(ForwardAllowed)
			{
				foreach(IInGrid Entry in Forward)
				{
					if(Entry is Tile FellowBranch && FellowBranch.Type == Items.ID.SLOPE)
					{
						if(SnapToGrid(LoopRotation(FellowBranch.RotationDegrees.y), 360, 4) != 0)
						{
							ForwardAllowed = false;
							break;
						}
					}
				}
			}
			if(BackwardAllowed)
			{
				foreach(IInGrid Entry in Backward)
				{
					if(Entry is Tile FellowBranch && FellowBranch.Type == Items.ID.SLOPE)
					{
						if(SnapToGrid(LoopRotation(FellowBranch.RotationDegrees.y), 360, 4) != 180)
						{
							BackwardAllowed = false;
							break;
						}
					}
				}
			}
			if(RightAllowed)
			{
				foreach(IInGrid Entry in Right)
				{
					if(Entry is Tile FellowBranch && FellowBranch.Type == Items.ID.SLOPE)
					{
						if(SnapToGrid(LoopRotation(FellowBranch.RotationDegrees.y), 360, 4) != 270)
						{
							RightAllowed = false;
							break;
						}
					}
				}
			}
			if(LeftAllowed)
			{
				foreach(IInGrid Entry in Left)
				{
					if(Entry is Tile FellowBranch && FellowBranch.Type == Items.ID.SLOPE)
					{
						if(SnapToGrid(LoopRotation(FellowBranch.RotationDegrees.y), 360, 4) != 90)
						{
							LeftAllowed = false;
							break;
						}
					}
				}
			}
		}

		if(ForwardAllowed)
			TryConnectTileToGridSpace(Branch, Forward);
		if(BackwardAllowed)
			TryConnectTileToGridSpace(Branch, Backward);
		if(RightAllowed)
			TryConnectTileToGridSpace(Branch, Right);
		if(LeftAllowed)
			TryConnectTileToGridSpace(Branch, Left);
	}


	public static void TryConnectTileToGridSpace(Tile Branch, List<IInGrid> Space)
	{
		foreach(IInGrid Entry in Space)
		{
			if(Entry is Tile Other)
			{
				switch(Other.Type) //TODO: Handle other walkable tiles
				{
					case(Items.ID.PLATFORM):
					case(Items.ID.SLOPE):
						Pathfinder.ConnectPoints(Branch.PathId, Other.PathId, true);
						break;

					default:
						break;
				}
			}
		}
	}


	//Name is the string GUID name of the structure to be removed
	[Remote]
	public void RemoveTile(string Name)
	{
		if(TilesRoot.HasNode(Name))
		{
			Tile Branch = TilesRoot.GetNode(Name) as Tile;
			Tuple<int,int> ChunkTuple = GetChunkTuple(Branch.Translation);
			Chunks[ChunkTuple].Tiles.Remove(Branch);
			if(Chunks[ChunkTuple].Tiles.Count <= 0 && Chunks[ChunkTuple].Items.Count <= 0)
			{
				//If the chunk is empty then remove it
				Chunks.Remove(ChunkTuple);
			}

			Grid.QueueUpdateNearby(Branch.Translation);
			Grid.QueueRemoveItem(Branch);
			Branch.OnRemove();
			Branch.QueueFree();
		}
	}


	[Remote]
	public void RemoveDroppedItem(string Guid) //NOTE: Make sure to remove from World.ItemList after client callsite
	{
		if(EntitiesRoot.HasNode(Guid))
		{
			DroppedItem Item = EntitiesRoot.GetNode(Guid) as DroppedItem;
			Tuple<int,int> ChunkTuple = GetChunkTuple(Item.Translation);
			Chunks[ChunkTuple].Items.Remove(Item);
			if(Chunks[ChunkTuple].Tiles.Count <= 0 && Chunks[ChunkTuple].Items.Count <= 0)
			{
				//If the chunk is empty then remove it
				Chunks.Remove(ChunkTuple);
			}

			Grid.QueueRemoveItem(Item);
			ItemList.Remove(Item);
			Item.QueueFree();
		}
	}


	[Remote]
	public void InitialNetWorldLoad(int Id, Vector3 PlayerPosition, int RenderDistance)
	{
		RequestChunks(Id, PlayerPosition, RenderDistance);
		Net.Players[Id].SetFreeze(false);
		Net.Players[Id].GiveDefaultItems();
	}


	[Remote]
	public void RequestChunks(int Id, Vector3 PlayerPosition, int RenderDistance) //Can be called non-rpc by passing int id
	{
		if(!GetTree().IsNetworkServer())
		{
			RpcId(Net.ServerId, nameof(RequestChunks), new object[] {Id, PlayerPosition, RenderDistance});
			return; //If not already on the server run on server and return early on client
		}

		if(!Net.PeerList.Contains(Id)) {return;}

		ChunkLoadDistances[Id] = RenderDistance;

		List<Tuple<int,int>> LoadedChunks = RemoteLoadedChunks[Id];
		foreach(KeyValuePair<System.Tuple<int, int>, ChunkClass> Chunk in Chunks)
		{
			Vector3 ChunkPos = new Vector3(Chunk.Key.Item1, 0, Chunk.Key.Item2);
			Tuple<int,int> ChunkTuple = GetChunkTuple(ChunkPos);
			if(ChunkPos.DistanceTo(new Vector3(PlayerPosition.x,0,PlayerPosition.z)) <= RenderDistance*(PlatformSize*9))
			{
				//This chunk is close enough to the player that we should send it along
				if(!LoadedChunks.Contains(ChunkTuple))
				{
					//If not already in the list of loaded chunks for this client then add it
					RemoteLoadedChunks[Id].Add(ChunkTuple);
					//And send it
					SendChunk(Id, ChunkTuple);
				}
				//If already loaded then don't send it
			}
			else
			{
				//This chunk is to far away
				if(LoadedChunks.Contains(ChunkTuple))
				{
					//If it is in the list of loaded chunks for this client then remove
					RemoteLoadedChunks[Id].Remove(ChunkTuple);
				}
			}
		}
	}


	static void SendChunk(int Id, Tuple<int,int> ChunkLocation)
	{
		Self.RpcId(Id, nameof(PrepareChunkSpace), new Vector2(ChunkLocation.Item1, ChunkLocation.Item2));

		foreach(Tile Branch in Chunks[ChunkLocation].Tiles)
		{
			Self.RpcId(Id, nameof(PlaceWithName), new object[] {Branch.Type, Branch.Translation, Branch.RotationDegrees, Branch.OwnerId, Branch.Name});
		}

		foreach(DroppedItem Item in Chunks[ChunkLocation].Items)
		{
			Self.RpcId(Id, nameof(DropOrUpdateItem), Item.Type, Item.Translation, Item.Momentum, Item.Name);
		}
	}


	public static int SaveChunk(Tuple<int,int> ChunkTuple, string SaveNameArg)
	{
		string SerializedChunk = new SavedChunk(ChunkTuple).ToJson();

		Directory SaveDir = new Directory();
		if(!SaveDir.DirExists($"user://Saves/{SaveNameArg}/Chunks"))
		{
			SaveDir.MakeDirRecursive($"user://Saves/{SaveNameArg}/Chunks");
		}
		System.IO.File.WriteAllText($"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/Chunks/{ChunkTuple.ToString()}.json", SerializedChunk);

		int SaveCount = 0;
		foreach(Tile Branch in Chunks[ChunkTuple].Tiles) //I hate to do this because it is rather inefficient
		{
			if(Branch.OwnerId != 0)
			{
				SaveCount += 1;
			}
		}
		return SaveCount;
	}


	public static Tuple<bool,int> LoadChunk(string ToLoad) //Doesn't actually have to be a single chunk
	{
		SavedChunk LoadedChunk;
		try
		{
			LoadedChunk = Newtonsoft.Json.JsonConvert.DeserializeObject<SavedChunk>(ToLoad);
		}
		catch(Newtonsoft.Json.JsonReaderException)
		{
			return new Tuple<bool,int>(false, 0);
		}

		int PlaceCount = 0;
		foreach(SavedTile SavedBranch in LoadedChunk.S)
		{
			Tuple<Items.ID,Vector3,Vector3> Info = SavedBranch.GetInfoOrNull();
			if(Info != null)
			{
				Place(Info.Item1, Info.Item2, Info.Item3, 1);
				PlaceCount++;
			}
		}
		return new Tuple<bool,int>(true, PlaceCount);
	}


	[Remote]
	public void PrepareChunkSpace(Vector2 Pos) //Run on the client to clear a chunk's area before being populated from the server
	{
		ChunkClass ChunkToFree;
		if(Chunks.TryGetValue(new Tuple<int,int>((int)Pos.x, (int)Pos.y), out ChunkToFree)) //Chunk might not exist
		{
			foreach(Tile Branch in ChunkToFree.Tiles)
			{
				Branch.Free();
			}

			List<DroppedItem> ItemsToRemove = new List<DroppedItem>();
			foreach(DroppedItem Item in ChunkToFree.Items)
			{
				ItemsToRemove.Add(Item);
			}
			foreach(DroppedItem Item in ItemsToRemove)
			{
				Item.Remove();
			}

			Chunks.Remove(new Tuple<int,int>((int)Pos.x, (int)Pos.y));
		}
	}


	//Should be able to be called without RPC yet only run on server
	//Has to be non-static to be RPC-ed
	[Remote]
	public void DropItem(Items.ID Type, Vector3 Position, Vector3 BaseMomentum)
	{
		if(Self.GetTree().NetworkPeer != null)
		{
			if(Self.GetTree().IsNetworkServer())
			{
				string Name = System.Guid.NewGuid().ToString();
				DropOrUpdateItem(Type, Position, BaseMomentum, Name);
				Net.SteelRpc(Self, nameof(DropOrUpdateItem), Type, Position, BaseMomentum, Name);
			}
			else
			{
				Self.RpcId(Net.ServerId, nameof(DropItem), Type, Position, BaseMomentum);
			}
		}
	}


	//Has to be non-static to be RPC-ed
	[Remote]
	public void DropOrUpdateItem(Items.ID Type, Vector3 Position, Vector3 BaseMomentum, string Name) //Performs the actual drop
	{
		if(EntitiesRoot.HasNode(Name))
		{
			DroppedItem Instance = EntitiesRoot.GetNode<DroppedItem>(Name);
			Instance.Translation = Position;
			Instance.Momentum = BaseMomentum;
			Instance.PhysicsEnabled = true;
		}
		else
		{
			Vector3 LevelPlayerPos = new Vector3(Game.PossessedPlayer.Translation.x,0,Game.PossessedPlayer.Translation.z);

			if(GetChunkPos(Position).DistanceTo(LevelPlayerPos) <= Game.ChunkRenderDistance*(PlatformSize*9))
			{
				DroppedItem ToDrop = DroppedItemScene.Instance() as DroppedItem;
				ToDrop.Translation = Position;
				ToDrop.Momentum = BaseMomentum;
				ToDrop.Type = Type;
				ToDrop.Name = Name;
				ToDrop.GetNode<MeshInstance>("MeshInstance").Mesh = Items.Meshes[Type];

				AddItemToChunk(ToDrop);
				ItemList.Add(ToDrop);
				EntitiesRoot.AddChild(ToDrop);
			}
		}
	}


	public override void _Process(float Delta)
	{
		if(IsOpen)
		{
			Time += Delta;
			if(Time >= 60f*DayNightMinutes)
				Time -= 60*DayNightMinutes;
			Time = Clamp(Time, 0, 60f*DayNightMinutes);

			Grid.DoWork();

			WorldSky.SunLatitude = Time * (360f / (DayNightMinutes*60f));

			float LightTime = Time;
			if(LightTime > 15f*DayNightMinutes)
				LightTime = Clamp((30f*DayNightMinutes)-LightTime, 0, 15f*DayNightMinutes);
			float Power = Clamp(((LightTime) / (DayNightMinutes*30f))*5f, 0, 1);

			WorldEnv.AmbientLightEnergy = Clamp(Power, 0.1f, 1);

			Color DaySkyTop = new Color(179f/255f, 213f/255f, 255f/255f, 1);
			Color MorningSkyTop = new Color(34f/255f, 50f/255f, 78f/255f, 1);
			Color NightSkyTop = new Color(27.2f/255f, 40f/255f, 62.4f/255f, 1);

			Color DayHorizon = new Color(70f/255f, 146f/255f, 255f/255f, 1);
			Color MorningHorizon = new Color(222f/255f, 129f/255f, 73f/255f, 1);
			Color NightHorizon = NightSkyTop;


			Color DayGround = new Color(134f/255f, 195f/255f, 255f/255f, 1);
			Color MorningGround = new Color(20f/255f, 29f/255f, 44f/255f, 1);

			if(Time <= DayNightMinutes*60/2)
			{
				WorldSky.SkyTopColor = SteelMath.LerpColor(MorningSkyTop, DaySkyTop, Power);
				WorldSky.SkyHorizonColor = SteelMath.LerpColor(MorningHorizon, DayHorizon, Power);
			}
			else
			{
				float Diff;
				if(Time < DayNightMinutes*60/4*3)
					Diff = (DayNightMinutes*60/4*3 - Time) / (DayNightMinutes*60/4);
				else
					Diff = (Time - DayNightMinutes*60/4*3) / (DayNightMinutes*60/4);
				Diff = Mathf.Clamp(Diff, 0, 1);
				Diff = Pow(Diff, 4);

				WorldSky.SkyTopColor = SteelMath.LerpColor(NightSkyTop, MorningSkyTop, Diff);
				WorldSky.SkyHorizonColor = SteelMath.LerpColor(NightHorizon, MorningHorizon, Diff);
			}
			WorldSky.GroundHorizonColor = WorldSky.SkyHorizonColor;

			WorldSky.GroundBottomColor = SteelMath.LerpColor(MorningGround, DayGround, Power);
		}
	}
}

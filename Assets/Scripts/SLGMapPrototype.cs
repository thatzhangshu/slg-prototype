using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VibeSLG
{
    public enum TileType : byte
    {
        Land, Mountain, Water, City, Wood, Food, Stone, Iron
    }

    [Serializable]
    public class MapSaveData
    {
        public int width;
        public int height;
        public int seed;
        public byte[] types;
        public byte[] elevations;
    }

    public sealed class SLGMapPrototype : MonoBehaviour
    {
        [Header("Map")]
        public int mapWidth = 256;
        public int mapHeight = 256;
        public int seed = 20260612;
        public int chunkSize = 16;
        public int visibleChunkRadius = 3;
        public float tileSize = 1f;
        public float heightStep = 0.35f;

        [Header("References")]
        public Camera mapCamera;

        private MapSaveData data;
        private readonly Dictionary<Vector2Int, ChunkView> activeChunks = new Dictionary<Vector2Int, ChunkView>();
        private readonly Stack<ChunkView> chunkPool = new Stack<ChunkView>();
        private Material[] tileMaterials;
        private Material highlightMaterial;
        private GameObject hoverMarker;
        private GameObject selectionMarker;
        private Vector2Int hoveredTile = new Vector2Int(-1, -1);
        private Vector2Int selectedTile = new Vector2Int(-1, -1);
        private Vector2Int lastCenterChunk = new Vector2Int(int.MinValue, int.MinValue);
        private string status = "";
        private float statusUntil;

        private string SavePath => Path.Combine(Application.persistentDataPath, "slg_map_save.json");

        private static readonly Color[] TileColors =
        {
            new Color(0.40f, 0.57f, 0.29f), // land
            new Color(0.34f, 0.34f, 0.31f), // mountain
            new Color(0.12f, 0.42f, 0.68f), // water
            new Color(0.75f, 0.52f, 0.23f), // city
            new Color(0.16f, 0.43f, 0.20f), // wood
            new Color(0.74f, 0.66f, 0.22f), // food
            new Color(0.48f, 0.48f, 0.50f), // stone
            new Color(0.38f, 0.31f, 0.28f)  // iron
        };

        private void Awake()
        {
            if (mapCamera == null) mapCamera = Camera.main;
            CreateMaterials();
            CreateMarker(ref hoverMarker, new Color(1f, 0.92f, 0.18f, 0.65f), "Hover Marker");
            CreateMarker(ref selectionMarker, new Color(1f, 0.28f, 0.12f, 0.75f), "Selection Marker");
            GenerateMap();
            if (Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "-slgSmokeTest"))
            {
                SaveMap();
                LoadMap();
                Debug.Log($"SLG_SMOKE_TEST_OK map={mapWidth}x{mapHeight} chunks={activeChunks.Count}");
                Application.Quit();
            }
        }

        private void Update()
        {
            UpdateVisibleChunks();
            UpdatePointer();
            if (Input.GetKeyDown(KeyCode.F5)) SaveMap();
            if (Input.GetKeyDown(KeyCode.F9)) LoadMap();
            if (Input.GetKeyDown(KeyCode.R)) GenerateMap();
        }

        public void GenerateMap()
        {
            var random = new System.Random(seed);
            float ox = random.Next(-100000, 100000);
            float oy = random.Next(-100000, 100000);
            data = new MapSaveData
            {
                width = mapWidth,
                height = mapHeight,
                seed = seed,
                types = new byte[mapWidth * mapHeight],
                elevations = new byte[mapWidth * mapHeight]
            };

            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    float continental = Mathf.PerlinNoise(ox + x * 0.018f, oy + y * 0.018f);
                    float detail = Mathf.PerlinNoise(ox * 0.3f + x * 0.075f, oy * 0.3f + y * 0.075f);
                    int i = Index(x, y);
                    TileType type;
                    byte elevation;
                    if (continental < 0.34f)
                    {
                        type = TileType.Water;
                        elevation = 0;
                    }
                    else if (continental > 0.72f)
                    {
                        type = TileType.Mountain;
                        elevation = (byte)Mathf.Clamp(3 + Mathf.FloorToInt(detail * 3f), 3, 5);
                    }
                    else
                    {
                        elevation = (byte)Mathf.Clamp(1 + Mathf.FloorToInt(continental * 2f), 1, 2);
                        float resourceRoll = Mathf.PerlinNoise(ox - x * 0.11f, oy + y * 0.11f);
                        if (resourceRoll > 0.82f) type = TileType.Wood;
                        else if (resourceRoll < 0.12f) type = TileType.Food;
                        else if (detail > 0.82f) type = TileType.Stone;
                        else if (detail < 0.10f) type = TileType.Iron;
                        else type = TileType.Land;
                    }
                    data.types[i] = (byte)type;
                    data.elevations[i] = elevation;
                }
            }

            PlaceCities(random, Mathf.Max(8, mapWidth * mapHeight / 4096));
            RebuildVisible();
            ShowStatus($"已生成 {mapWidth}×{mapHeight} 地图，种子 {seed}");
        }

        private void PlaceCities(System.Random random, int count)
        {
            int attempts = count * 80;
            while (count > 0 && attempts-- > 0)
            {
                int x = random.Next(5, mapWidth - 5);
                int y = random.Next(5, mapHeight - 5);
                int i = Index(x, y);
                TileType current = (TileType)data.types[i];
                if (current == TileType.Water || current == TileType.Mountain) continue;
                data.types[i] = (byte)TileType.City;
                data.elevations[i] = 2;
                count--;
            }
        }

        private void UpdateVisibleChunks()
        {
            if (mapCamera == null || data == null) return;
            Vector3 p = mapCamera.transform.position;
            Ray centerRay = mapCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (groundPlane.Raycast(centerRay, out float centerDistance))
                p = centerRay.GetPoint(centerDistance);
            Vector2Int center = new Vector2Int(
                Mathf.Clamp(Mathf.FloorToInt(p.x / (chunkSize * tileSize)), 0, (mapWidth - 1) / chunkSize),
                Mathf.Clamp(Mathf.FloorToInt(p.z / (chunkSize * tileSize)), 0, (mapHeight - 1) / chunkSize));
            if (center == lastCenterChunk) return;
            lastCenterChunk = center;

            var needed = new HashSet<Vector2Int>();
            for (int cy = center.y - visibleChunkRadius; cy <= center.y + visibleChunkRadius; cy++)
            {
                for (int cx = center.x - visibleChunkRadius; cx <= center.x + visibleChunkRadius; cx++)
                {
                    if (cx < 0 || cy < 0 || cx * chunkSize >= mapWidth || cy * chunkSize >= mapHeight) continue;
                    var key = new Vector2Int(cx, cy);
                    needed.Add(key);
                    if (!activeChunks.ContainsKey(key)) ActivateChunk(key);
                }
            }

            var remove = new List<Vector2Int>();
            foreach (var pair in activeChunks)
                if (!needed.Contains(pair.Key)) remove.Add(pair.Key);
            foreach (var key in remove)
            {
                ChunkView view = activeChunks[key];
                activeChunks.Remove(key);
                view.gameObject.SetActive(false);
                chunkPool.Push(view);
            }
        }

        private void ActivateChunk(Vector2Int key)
        {
            ChunkView view = chunkPool.Count > 0 ? chunkPool.Pop() : CreateChunkView();
            view.gameObject.name = $"Chunk {key.x},{key.y}";
            view.gameObject.SetActive(true);
            view.transform.position = new Vector3(key.x * chunkSize * tileSize, 0f, key.y * chunkSize * tileSize);
            BuildChunkMesh(view.mesh, key);
            activeChunks.Add(key, view);
        }

        private ChunkView CreateChunkView()
        {
            var go = new GameObject("Chunk");
            go.transform.SetParent(transform, false);
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = tileMaterials;
            var mesh = new Mesh { name = "Runtime Chunk Mesh" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            filter.sharedMesh = mesh;
            return new ChunkView(go, mesh);
        }

        private void BuildChunkMesh(Mesh mesh, Vector2Int key)
        {
            var vertices = new List<Vector3>(chunkSize * chunkSize * 8);
            var triangles = new List<int>[tileMaterials.Length];
            for (int i = 0; i < triangles.Length; i++) triangles[i] = new List<int>();

            int startX = key.x * chunkSize;
            int startY = key.y * chunkSize;
            int endX = Mathf.Min(startX + chunkSize, mapWidth);
            int endY = Mathf.Min(startY + chunkSize, mapHeight);
            for (int y = startY; y < endY; y++)
            {
                for (int x = startX; x < endX; x++)
                {
                    int i = Index(x, y);
                    TileType type = (TileType)data.types[i];
                    float h = GetVisualHeight(x, y);
                    AddBlock(vertices, triangles[(int)type], (x - startX) * tileSize, (y - startY) * tileSize, h);
                }
            }

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.subMeshCount = triangles.Length;
            for (int i = 0; i < triangles.Length; i++) mesh.SetTriangles(triangles[i], i);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        private void AddBlock(List<Vector3> v, List<int> t, float x, float z, float h)
        {
            float gap = tileSize * 0.035f;
            float x0 = x + gap;
            float z0 = z + gap;
            float x1 = x + tileSize - gap;
            float z1 = z + tileSize - gap;
            float bottom = -0.12f;
            AddFace(v, t, new Vector3(x0, h, z0), new Vector3(x0, h, z1), new Vector3(x1, h, z1), new Vector3(x1, h, z0));
            AddFace(v, t, new Vector3(x0, bottom, z1), new Vector3(x0, bottom, z0), new Vector3(x0, h, z0), new Vector3(x0, h, z1));
            AddFace(v, t, new Vector3(x1, bottom, z0), new Vector3(x1, bottom, z1), new Vector3(x1, h, z1), new Vector3(x1, h, z0));
            AddFace(v, t, new Vector3(x0, bottom, z0), new Vector3(x1, bottom, z0), new Vector3(x1, h, z0), new Vector3(x0, h, z0));
            AddFace(v, t, new Vector3(x1, bottom, z1), new Vector3(x0, bottom, z1), new Vector3(x0, h, z1), new Vector3(x1, h, z1));
        }

        private static void AddFace(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        private void UpdatePointer()
        {
            if (mapCamera == null || data == null) return;
            Ray ray = mapCamera.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out float distance))
            {
                hoverMarker.SetActive(false);
                return;
            }
            Vector3 p = ray.GetPoint(distance);
            int x = Mathf.FloorToInt(p.x / tileSize);
            int y = Mathf.FloorToInt(p.z / tileSize);
            if (!InBounds(x, y))
            {
                hoveredTile = new Vector2Int(-1, -1);
                hoverMarker.SetActive(false);
                return;
            }
            hoveredTile = new Vector2Int(x, y);
            PositionMarker(hoverMarker, hoveredTile, 0.025f);
            if (Input.GetMouseButtonDown(0) && !Input.GetKey(KeyCode.LeftAlt))
            {
                selectedTile = hoveredTile;
                PositionMarker(selectionMarker, selectedTile, 0.045f);
            }
        }

        private void PositionMarker(GameObject marker, Vector2Int tile, float offset)
        {
            marker.SetActive(true);
            marker.transform.position = new Vector3((tile.x + 0.5f) * tileSize, GetVisualHeight(tile.x, tile.y) + offset, (tile.y + 0.5f) * tileSize);
            marker.transform.localScale = new Vector3(tileSize * 0.92f, 1f, tileSize * 0.92f);
        }

        private void CreateMarker(ref GameObject marker, Color color, string markerName)
        {
            marker = GameObject.CreatePrimitive(PrimitiveType.Quad);
            marker.name = markerName;
            marker.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Destroy(marker.GetComponent<Collider>());
            var material = new Material(FindShader()) { color = color };
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_ZWrite", 0f);
            material.renderQueue = 3100;
            marker.GetComponent<Renderer>().sharedMaterial = material;
            marker.SetActive(false);
        }

        private void CreateMaterials()
        {
            tileMaterials = new Material[TileColors.Length];
            Shader shader = FindShader();
            for (int i = 0; i < tileMaterials.Length; i++)
            {
                tileMaterials[i] = new Material(shader) { name = ((TileType)i).ToString(), color = TileColors[i] };
                if (tileMaterials[i].HasProperty("_Smoothness")) tileMaterials[i].SetFloat("_Smoothness", i == (int)TileType.Water ? 0.75f : 0.08f);
            }
        }

        private static Shader FindShader()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            return shader;
        }

        public void SaveMap()
        {
            File.WriteAllText(SavePath, JsonUtility.ToJson(data));
            ShowStatus($"地图已保存：{SavePath}");
        }

        public void LoadMap()
        {
            if (!File.Exists(SavePath))
            {
                ShowStatus("尚无存档，请先按 F5 保存");
                return;
            }
            data = JsonUtility.FromJson<MapSaveData>(File.ReadAllText(SavePath));
            mapWidth = data.width;
            mapHeight = data.height;
            seed = data.seed;
            RebuildVisible();
            ShowStatus("地图存档已读取");
        }

        private void RebuildVisible()
        {
            foreach (var pair in activeChunks)
            {
                pair.Value.gameObject.SetActive(false);
                chunkPool.Push(pair.Value);
            }
            activeChunks.Clear();
            lastCenterChunk = new Vector2Int(int.MinValue, int.MinValue);
            UpdateVisibleChunks();
        }

        private float GetVisualHeight(int x, int y)
        {
            return data.elevations[Index(x, y)] * heightStep + ((TileType)data.types[Index(x, y)] == TileType.Water ? 0.04f : 0f);
        }

        private int Index(int x, int y) => y * mapWidth + x;
        private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < mapWidth && y < mapHeight;
        private void ShowStatus(string message) { status = message; statusUntil = Time.unscaledTime + 4f; Debug.Log(message); }

        private void OnGUI()
        {
            GUI.Box(new Rect(14, 14, 355, 150), "大型 SLG 方块地图 - 第一阶段原型");
            GUI.Label(new Rect(28, 42, 330, 22), $"地图：{mapWidth}×{mapHeight}  活跃分块：{activeChunks.Count}  池：{chunkPool.Count}");
            GUI.Label(new Rect(28, 64, 330, 22), "中键/Alt+左键拖拽 | 滚轮缩放 | 左键选格");
            GUI.Label(new Rect(28, 86, 330, 22), "R 重新生成 | F5 保存 | F9 读取");
            if (InBounds(hoveredTile.x, hoveredTile.y))
            {
                TileType type = (TileType)data.types[Index(hoveredTile.x, hoveredTile.y)];
                GUI.Label(new Rect(28, 108, 330, 22), $"悬停：({hoveredTile.x}, {hoveredTile.y})  {TileName(type)}");
            }
            if (Time.unscaledTime < statusUntil) GUI.Label(new Rect(28, 130, 330, 22), status);
        }

        private static string TileName(TileType type)
        {
            string[] names = { "普通土地", "山地", "水域", "城池", "木材", "粮草", "石头", "铁矿" };
            return names[(int)type];
        }

        private sealed class ChunkView
        {
            public readonly GameObject gameObject;
            public readonly Transform transform;
            public readonly Mesh mesh;
            public ChunkView(GameObject gameObject, Mesh mesh)
            {
                this.gameObject = gameObject;
                transform = gameObject.transform;
                this.mesh = mesh;
            }
        }
    }

    public sealed class SLGCameraController : MonoBehaviour
    {
        public float dragSpeed = 0.035f;
        public float zoomSpeed = 2.2f;
        public float minOrthographicSize = 7f;
        public float maxOrthographicSize = 22f;
        public Vector2 mapBounds = new Vector2(256f, 256f);
        private Vector3 lastMouse;
        private Camera mapCamera;

        private void Awake()
        {
            mapCamera = GetComponent<Camera>();
        }

        private void Update()
        {
            bool dragging = Input.GetMouseButton(2) || (Input.GetMouseButton(0) && Input.GetKey(KeyCode.LeftAlt));
            if (dragging)
            {
                Vector3 delta = Input.mousePosition - lastMouse;
                float scale = mapCamera != null
                    ? Mathf.Lerp(0.45f, 1.35f, Mathf.InverseLerp(minOrthographicSize, maxOrthographicSize, mapCamera.orthographicSize))
                    : 1f;
                transform.position += (-transform.right * delta.x - Vector3.Cross(transform.right, Vector3.up) * delta.y) * dragSpeed * scale;
            }
            lastMouse = Input.mousePosition;

            float wheel = Input.mouseScrollDelta.y;
            if (mapCamera != null && Mathf.Abs(wheel) > 0.01f)
                mapCamera.orthographicSize = Mathf.Clamp(mapCamera.orthographicSize - wheel * zoomSpeed, minOrthographicSize, maxOrthographicSize);

            Vector3 p = transform.position;
            p.x = Mathf.Clamp(p.x, 0f, mapBounds.x);
            p.z = Mathf.Clamp(p.z, 0f, mapBounds.y);
            transform.position = p;
        }
    }
}

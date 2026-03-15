using System.Collections.Generic;
using System.IO;
using System;
using UnityEngine;
using UnityEngine.UIElements;
using JetBrains.Annotations;

public class WorldGenerator : MonoBehaviour
{
    [Header("Configurações de Mundo")]
    public GameObject chunkPrefab;
    public Transform player;
    public int viewDistance = 2;
    public float noiseScale = 0.05f;
    public List<StructureData> structures;

    [Header("Ferramentas de Desenvolvimento")]
    [Tooltip("Deleta todos os arquivos .dat salvos ao iniciar o jogo.")]
    public bool clearSaveOnStart = false;

    private Dictionary<Vector2Int, MapGenerator> activeChunks = new Dictionary<Vector2Int, MapGenerator>();
    private Dictionary<Vector2Int, MapGenerator> pendingChunks = new Dictionary<Vector2Int, MapGenerator>();
    private HashSet<Vector2Int> failedChunks = new HashSet<Vector2Int>();
    private List<Vector2Int> generationQueue = new List<Vector2Int>();
    private List<Vector2Int> chunksAguardandoDecoracao = new();
    private Vector2Int? currentlyGenerating = null;

    private Vector2Int lastPlayerChunk;
    private Transform chunksContainer;
    private string savePath;
    private Vector2Int chunkSize;
    private float cachedCellSize;

    [Serializable]
    public struct StructureSaveData
    {
        public string structureName;
        public Vector3 structureWorldPosition;
        public float raioDeIsolamento;
    }
    public List<StructureSaveData> savedStructures = new();

    // Unity Lifecycle

    void Start()
    {
        GameObject container = new GameObject("Chunks");
        chunksContainer = container.transform;

        savePath = Application.persistentDataPath + "/map_data/";
        if (!Directory.Exists(savePath)) Directory.CreateDirectory(savePath);

        if (clearSaveOnStart) ClearSaveData();

        var mgTemplate = chunkPrefab.GetComponent<MapGenerator>();
        chunkSize = mgTemplate.chunkSize;
        cachedCellSize = chunkPrefab.GetComponent<Grid>().cellSize.x;

        lastPlayerChunk = GetPlayerChunkPos();
        GenerateInitialChunks(lastPlayerChunk);
    }

    void Update()
    {
        Vector2Int currentPlayerChunk = GetPlayerChunkPos();
        if (currentPlayerChunk != lastPlayerChunk)
        {
            lastPlayerChunk = currentPlayerChunk;
            UpdateVisibleChunks(currentPlayerChunk);
            SortQueueByDistance(currentPlayerChunk);
        }

        if (currentlyGenerating == null)
            ProcessNextInQueue();
        else
            Debug.Log($"[WG] Gerando: {currentlyGenerating} | Fila: {generationQueue.Count} | Active: {activeChunks.Count} | Pending: {pendingChunks.Count}");
        ProcessarDecoracoes();
    }

    // Chunk Position

    private Vector2Int GetPlayerChunkPos()
    {
        return new Vector2Int(
            Mathf.FloorToInt(player.position.x / (chunkSize.x * cachedCellSize)),
            Mathf.FloorToInt(player.position.y / (chunkSize.y * cachedCellSize))
        );
    }

    private Vector3 ChunkWorldPos(Vector2Int pos)
    {
        return new Vector3(pos.x * chunkSize.x * cachedCellSize, pos.y * chunkSize.y * cachedCellSize, 0);
    }

    // Chunk Generation

    private void GenerateInitialChunks(Vector2Int center)
    {
        List<Vector2Int> positions = new List<Vector2Int>();
        for (int r = 0; r <= viewDistance; r++)
        {
            for (int x = -r; x <= r; x++)
            {
                for (int y = -r; y <= r; y++)
                {
                    if (Mathf.Abs(x) != r && Mathf.Abs(y) != r) continue;
                    positions.Add(new Vector2Int(center.x + x, center.y + y));
                }
            }
        }

        foreach (var pos in positions)
        {
            if (activeChunks.ContainsKey(pos)) continue;
            CreateOrLoadChunkSync(pos);
        }
    }

    private void CreateOrLoadChunkSync(Vector2Int pos)
    {
        string path = savePath + $"chunk_{pos.x}_{pos.y}.dat";
        Vector3 worldPos = ChunkWorldPos(pos);

        GameObject go = Instantiate(chunkPrefab, worldPos, Quaternion.identity, chunksContainer);
        MapGenerator mg = go.GetComponent<MapGenerator>();
        mg.Setup(player.gameObject, this);
        activeChunks.Add(pos, mg);

        if (File.Exists(path) && !failedChunks.Contains(pos))
        {
            mg.LoadFromData(File.ReadAllBytes(path));
            mg.SpawnEntities();
        }
        else
        {
            float noiseValue = Mathf.PerlinNoise(pos.x * noiseScale + 100.5f, pos.y * noiseScale + 100.5f);
            Dictionary<Vector2Int, Tile> halo = BuildHalo(pos);
            if (pos == Vector2Int.zero)
            {
                mg.ForceWaterChunk(halo);
            }
            else if (mg.GenerateChunk(halo, noiseValue))
            {
                failedChunks.Remove(pos);
                chunksAguardandoDecoracao.Add(pos);
            }
            else
            {
                Debug.LogWarning($"Contradição em {pos} (síncrono). Chunk marcada para nova tentativa.");
                failedChunks.Add(pos);
            }
        }

        NotifyNeighbors(pos, mg);
    }

    private void CreateOrLoadChunkAsync(Vector2Int pos)
    {
        string path = savePath + $"chunk_{pos.x}_{pos.y}.dat";
        Vector3 worldPos = ChunkWorldPos(pos);

        if (pendingChunks.TryGetValue(pos, out MapGenerator pendingMg))
        {
            pendingChunks.Remove(pos);
            activeChunks.Add(pos, pendingMg);
            pendingMg.tilemap.enabled = true;

            if (!pendingMg.IsGenerating)
                currentlyGenerating = null;
            return;
        }

        GameObject go = Instantiate(chunkPrefab, worldPos, Quaternion.identity, chunksContainer);
        MapGenerator mg = go.GetComponent<MapGenerator>();
        mg.Setup(player.gameObject, this);
        activeChunks.Add(pos, mg);

        if (File.Exists(path) && !failedChunks.Contains(pos))
        {
            mg.LoadFromData(File.ReadAllBytes(path));
            mg.SpawnEntities();
            NotifyNeighbors(pos, mg);
            currentlyGenerating = null;
        }
        else
        {
            float noiseValue = Mathf.PerlinNoise(pos.x * noiseScale + 100.5f, pos.y * noiseScale + 100.5f);
            Dictionary<Vector2Int, Tile> halo = BuildHalo(pos);

            mg.OnGenerationComplete = (completedMg, success) =>
            {
                Debug.Log($"[WG] Geração completa: {pos} sucesso={success} | currentlyGenerating={currentlyGenerating} | pending={pendingChunks.ContainsKey(pos)}");

                if (success)
                {
                    failedChunks.Remove(pos);
                    NotifyNeighbors(pos, completedMg);

                    if (activeChunks.ContainsKey(pos))
                        completedMg.SpawnEntities();
                    chunksAguardandoDecoracao.Add(pos);
                }
                else
                {
                    Debug.LogWarning($"Contradição em {pos}. Chunk marcada para nova tentativa ao reentrar.");
                    failedChunks.Add(pos);
                }

                if (pendingChunks.ContainsKey(pos))
                {
                    SaveAndDestroy(pos, completedMg);
                    pendingChunks.Remove(pos);
                }

                if (currentlyGenerating == pos)
                    currentlyGenerating = null;

                Debug.Log($"[WG] Após callback: currentlyGenerating={currentlyGenerating} | fila={generationQueue.Count}");
            };

            mg.GenerateChunkAsync(halo, noiseValue);
        }
    }

    // Chunk Visibility

    private void UpdateVisibleChunks(Vector2Int center)
    {
        HashSet<Vector2Int> currentCoords = new HashSet<Vector2Int>();

        for (int x = -viewDistance; x <= viewDistance; x++)
        {
            for (int y = -viewDistance; y <= viewDistance; y++)
            {
                Vector2Int chunkPos = new Vector2Int(center.x + x, center.y + y);
                currentCoords.Add(chunkPos);

                bool alreadyActive             = activeChunks.ContainsKey(chunkPos);
                bool alreadyPending            = pendingChunks.ContainsKey(chunkPos);
                bool alreadyInQueue            = generationQueue.Contains(chunkPos);
                bool currentlyBeingGenerated   = currentlyGenerating == chunkPos;

                if (!alreadyActive && !alreadyPending && !alreadyInQueue && !currentlyBeingGenerated)
                    EnqueueChunk(chunkPos, center);
            }
        }

        List<Vector2Int> toRemove = new List<Vector2Int>();
        foreach (var coord in activeChunks.Keys)
            if (!currentCoords.Contains(coord)) toRemove.Add(coord);

        foreach (var coord in toRemove)
        {
            MapGenerator mg = activeChunks[coord];
            activeChunks.Remove(coord);

            if (mg.IsGenerating)
            {
                pendingChunks.Add(coord, mg);
                mg.tilemap.enabled = false;
            }
            else
            {
                SaveAndDestroy(coord, mg);
            }
        }

        generationQueue.RemoveAll(pos => !currentCoords.Contains(pos));
    }

    // Generation Queue

    private void EnqueueChunk(Vector2Int pos, Vector2Int playerChunk)
    {
        generationQueue.Add(pos);
        SortQueueByDistance(playerChunk);
    }

    private void SortQueueByDistance(Vector2Int playerChunk)
    {
        generationQueue.Sort((a, b) =>
            (a - playerChunk).sqrMagnitude.CompareTo((b - playerChunk).sqrMagnitude));
    }

    private void ProcessNextInQueue()
    {
        generationQueue.RemoveAll(pos => activeChunks.ContainsKey(pos) || pendingChunks.ContainsKey(pos));

        if (generationQueue.Count == 0) return;

        Vector2Int pos = generationQueue[0];
        generationQueue.RemoveAt(0);

        currentlyGenerating = pos;
        CreateOrLoadChunkAsync(pos);
    }

    // Structure Generation

    public void EscanearEGerarEstruturas(Vector2Int chunkPos)
    {
        MapGenerator mapGenerator = activeChunks[chunkPos];

        foreach (StructureData structure in structures)
        {
            bool isGenerated = false;
            if (UnityEngine.Random.value <= structure.spawnChance)
            {
                for (int x = 0; x < chunkSize.x; x++)
                {
                    if (isGenerated) break;

                    for (int y = 0; y < chunkSize.y; y++)
                    {
                        if (isGenerated) break;

                        Vector3 worldPos = GetTileWorldPosition(chunkPos, x, y);

                        if (ValidarPlantaBaixa(worldPos, structure))
                        {
                            worldPos.x += (structure.structureDimensions.x - 1) * cachedCellSize / 2f;
                            worldPos.y += (structure.structureDimensions.y - 1) * cachedCellSize / 2f; 
                            
                            Instantiate(structure.structurePrefab, worldPos, Quaternion.identity);
                            RegistrarEstrutura(structure.structureName, worldPos, structure.raioDeIsolamento);
                            isGenerated = true;
                        }
                    }
                }
            }
        }
    }

    private bool ValidarPlantaBaixa(Vector3 posMundoInicial, StructureData planta)
    {
        foreach (StructureSaveData structure in savedStructures)
        {
            if (Vector3.Distance(structure.structureWorldPosition, posMundoInicial) < Mathf.Max(structure.raioDeIsolamento, planta.raioDeIsolamento)) return false;
        } 
        for(int x = 0; x < planta.structureDimensions.x; x++)
        {
            for(int y = 0; y < planta.structureDimensions.y; y++)
            {
                Vector3 tilePos = posMundoInicial + new Vector3(x * cachedCellSize, y * cachedCellSize, 0);
                Tile tile = GetTileAtWorldPosition(tilePos);
                if (tile == null) return false;
                bool isOnOverride = false;
                foreach(StructureData.LayerOverride layerOverride in planta.layerOverrides)
                {
                    if (isOnOverride) break;
                    foreach(Vector2Int coordinate in layerOverride.localCoordinates)
                    {
                        if (isOnOverride) break;;
                        if (new Vector2Int(x, y) == coordinate)
                        {
                            if (tile.metadata.camada == layerOverride.layer)
                            {
                                isOnOverride = true;
                                continue;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                }

                if (isOnOverride) continue;
                if (!planta.validBaseLayers.Contains(tile.metadata.camada)) return false;

            }            
        }
        return true;
    }

    public void ProcessarDecoracoes()
    {
        for (int i = chunksAguardandoDecoracao.Count - 1; i >= 0; i--)
        {
            if (TodosVizinhosProntos(chunksAguardandoDecoracao[i]))
            {
                EscanearEGerarEstruturas(chunksAguardandoDecoracao[i]);
                chunksAguardandoDecoracao.RemoveAt(i);
            }
        }
    }

    private bool TodosVizinhosProntos(Vector2Int pos)
    {
        for (int x = -1; x < 2; x++)
            for (int y = -1; y < 2; y++)
            {
                Vector2Int vizinho = pos + new Vector2Int(x, y);
                if (!activeChunks.TryGetValue(vizinho, out var mg) || mg.IsGenerating)
                    return false;
            }
        return true;
    }

    // Save / Destroy

    private void SaveAndDestroy(Vector2Int pos, MapGenerator mg)
    {
        if (!failedChunks.Contains(pos))
        {
            byte[] data = mg.GetChunkData();
            if (data != null) SaveChunkToDisk(pos, mg);
        }
        Destroy(mg.gameObject);
    }

    private void SaveChunkToDisk(Vector2Int pos, MapGenerator mg)
    {
        byte[] data = mg.GetChunkData();
        if (data != null) File.WriteAllBytes(savePath + $"chunk_{pos.x}_{pos.y}.dat", data);
    }

    [ContextMenu("Limpar Dados Salvos")]
    public void ClearSaveData()
    {
        if (!Directory.Exists(savePath)) return;

        int count = 0;
        foreach (string file in Directory.GetFiles(savePath, "*.dat"))
        {
            File.Delete(file);
            count++;
        }
        failedChunks.Clear();
        Debug.Log($"[WorldGenerator] {count} arquivo(s) .dat deletado(s) de {savePath}");
    }

    public void RegistrarEstrutura(string nome, Vector3 posicao, float raioDeIsolamento)
    {
        StructureSaveData structure = new()
        {
            structureName = nome,
            structureWorldPosition = posicao,
            raioDeIsolamento = raioDeIsolamento
        };
        savedStructures.Add(structure);
    }

    // Halo

    private Dictionary<Vector2Int, Tile> BuildHalo(Vector2Int pos)
    {
        var halo = new Dictionary<Vector2Int, Tile>();

        FillHaloEdge(pos, Vector2Int.left,  neighborCol: chunkSize.x - 1, isVertical: true,  haloFixed: 0,               halo: halo);
        FillHaloEdge(pos, Vector2Int.right, neighborCol: 0,               isVertical: true,  haloFixed: chunkSize.x + 1, halo: halo);
        FillHaloEdge(pos, Vector2Int.down,  neighborCol: chunkSize.y - 1, isVertical: false, haloFixed: 0,               halo: halo);
        FillHaloEdge(pos, Vector2Int.up,    neighborCol: 0,               isVertical: false, haloFixed: chunkSize.y + 1, halo: halo);

        AddHaloCorner(pos, new Vector2Int(-1, -1), new Vector2Int(chunkSize.x - 1, chunkSize.y - 1), new Vector2Int(0, 0),                             halo);
        AddHaloCorner(pos, new Vector2Int( 1, -1), new Vector2Int(0, chunkSize.y - 1),               new Vector2Int(chunkSize.x + 1, 0),               halo);
        AddHaloCorner(pos, new Vector2Int(-1,  1), new Vector2Int(chunkSize.x - 1, 0),               new Vector2Int(0, chunkSize.y + 1),               halo);
        AddHaloCorner(pos, new Vector2Int( 1,  1), new Vector2Int(0, 0),                             new Vector2Int(chunkSize.x + 1, chunkSize.y + 1), halo);

        return halo;
    }

    private void FillHaloEdge(Vector2Int pos, Vector2Int dir, int neighborCol, bool isVertical, int haloFixed, Dictionary<Vector2Int, Tile> halo)
    {
        Vector2Int neighborPos = pos + dir;
        int count = isVertical ? chunkSize.y : chunkSize.x;

        MapGenerator neighbor = null;
        activeChunks.TryGetValue(neighborPos, out neighbor);
        if (neighbor == null) pendingChunks.TryGetValue(neighborPos, out neighbor);

        if (neighbor != null)
        {
            for (int i = 0; i < count; i++)
            {
                Tile t = isVertical ? neighbor.GetTileAt(neighborCol, i) : neighbor.GetTileAt(i, neighborCol);
                if (t == null) continue;
                halo[isVertical ? new Vector2Int(haloFixed, i + 1) : new Vector2Int(i + 1, haloFixed)] = t;
            }
        }
        else
        {
            string path = savePath + $"chunk_{neighborPos.x}_{neighborPos.y}.dat";
            if (!File.Exists(path)) return;

            byte[] data = File.ReadAllBytes(path);
            MapGenerator refMg = GetAnyActiveChunk();
            if (refMg == null) return;

            for (int i = 0; i < count; i++)
            {
                int idx = isVertical ? (neighborCol * chunkSize.y + i) : (i * chunkSize.y + neighborCol);
                if (idx < 0 || idx >= data.Length) continue;
                Tile t = refMg.tilesetData.tileset[data[idx]];
                halo[isVertical ? new Vector2Int(haloFixed, i + 1) : new Vector2Int(i + 1, haloFixed)] = t;
            }
        }
    }

    private void AddHaloCorner(Vector2Int pos, Vector2Int diagDir, Vector2Int neighborCoord, Vector2Int haloCoord, Dictionary<Vector2Int, Tile> halo)
    {
        if (halo.ContainsKey(haloCoord)) return;

        Vector2Int neighborPos = pos + diagDir;

        MapGenerator neighbor = null;
        activeChunks.TryGetValue(neighborPos, out neighbor);
        if (neighbor == null) pendingChunks.TryGetValue(neighborPos, out neighbor);

        if (neighbor != null)
        {
            Tile t = neighbor.GetTileAt(neighborCoord.x, neighborCoord.y);
            if (t != null) halo[haloCoord] = t;
        }
        else
        {
            string path = savePath + $"chunk_{neighborPos.x}_{neighborPos.y}.dat";
            if (!File.Exists(path)) return;

            byte[] data = File.ReadAllBytes(path);
            MapGenerator refMg = GetAnyActiveChunk();
            if (refMg == null) return;

            int idx = neighborCoord.x * chunkSize.y + neighborCoord.y;
            if (idx < 0 || idx >= data.Length) return;
            halo[haloCoord] = refMg.tilesetData.tileset[data[idx]];
        }
    }

    // Neighbor Notification

    private void NotifyNeighbors(Vector2Int pos, MapGenerator newMg)
    {
        var sides = new[]
        {
            (dir: Vector2Int.left,  sourceCol: 0,               isVert: true,  haloFixed: chunkSize.x + 1),
            (dir: Vector2Int.right, sourceCol: chunkSize.x - 1, isVert: true,  haloFixed: 0),
            (dir: Vector2Int.down,  sourceCol: 0,               isVert: false, haloFixed: chunkSize.y + 1),
            (dir: Vector2Int.up,    sourceCol: chunkSize.y - 1, isVert: false, haloFixed: 0),
        };

        foreach (var s in sides)
        {
            Vector2Int neighborPos = pos + s.dir;

            MapGenerator neighborMg = null;
            activeChunks.TryGetValue(neighborPos, out neighborMg);
            if (neighborMg == null) pendingChunks.TryGetValue(neighborPos, out neighborMg);
            if (neighborMg == null) continue;

            var haloUpdate = new Dictionary<Vector2Int, Tile>();
            int count = s.isVert ? chunkSize.y : chunkSize.x;

            for (int i = 0; i < count; i++)
            {
                Tile t = s.isVert ? newMg.GetTileAt(s.sourceCol, i) : newMg.GetTileAt(i, s.sourceCol);
                if (t == null) continue;
                haloUpdate[s.isVert ? new Vector2Int(s.haloFixed, i + 1) : new Vector2Int(i + 1, s.haloFixed)] = t;
            }

            if (haloUpdate.Count > 0)
                neighborMg.UpdateHaloAndRepropagate(haloUpdate);
        }
    }

    // Helpers

    private MapGenerator GetAnyActiveChunk()
    {
        foreach (var mg in activeChunks.Values) if (mg != null) return mg;
        foreach (var mg in pendingChunks.Values) if (mg != null) return mg;
        return chunkPrefab.GetComponent<MapGenerator>();
    }


    public Vector3 GetTileWorldPosition(Vector2Int chunkPos, int localX, int localY)
    {
        Vector3 chunkOrigin = ChunkWorldPos(chunkPos);
        return new Vector3(
            chunkOrigin.x + localX * cachedCellSize,
            chunkOrigin.y + localY * cachedCellSize,
            0
        );
    }

    public bool IsChunkActive(Vector2Int chunkPos)
    {
        return activeChunks.ContainsKey(chunkPos) || pendingChunks.ContainsKey(chunkPos);
    }

    public Vector2Int GetChunkPosFromWorld(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / (chunkSize.x * cachedCellSize)),
            Mathf.FloorToInt(worldPos.y / (chunkSize.y * cachedCellSize))
        );
    }

    public Tile GetTileAtPlayerPosition()
    {
        Vector2Int chunkPos = GetPlayerChunkPos();

        if (!activeChunks.TryGetValue(chunkPos, out MapGenerator chunk))
            return null;

        float relativeX = player.position.x - chunk.transform.position.x;
        float relativeY = player.position.y - chunk.transform.position.y;

        int localX = Mathf.Clamp(Mathf.FloorToInt(relativeX / cachedCellSize), 0, chunkSize.x - 1);
        int localY = Mathf.Clamp(Mathf.FloorToInt(relativeY / cachedCellSize), 0, chunkSize.y - 1);

        return chunk.GetTileAt(localX, localY);
    }

    public Tile GetTileAtWorldPosition(Vector3 worldPos)
    {
        Vector2Int chunkPos = new Vector2Int(
            Mathf.FloorToInt(worldPos.x / (chunkSize.x * cachedCellSize)),
            Mathf.FloorToInt(worldPos.y / (chunkSize.y * cachedCellSize))
        );

        if (activeChunks.TryGetValue(chunkPos, out MapGenerator chunk))
        {
            float relativeX = worldPos.x - chunk.transform.position.x;
            float relativeY = worldPos.y - chunk.transform.position.y;

            int localX = Mathf.FloorToInt(relativeX / cachedCellSize);
            int localY = Mathf.FloorToInt(relativeY / cachedCellSize);

            return chunk.GetTileAt(localX, localY);
        }

        return null;
    }

    // Player / World Transition

    public void TryGoOut(Camera camera)
    {
        PlayerMovement boatMov = FindFirstObjectByType<PlayerMovement>();
        if (boatMov == null) return;

        GameObject barcoObj   = boatMov.gameObject;
        GameObject capitãoObj = boatMov.capitão;

        if (boatMov.isOnWater)
        {
            Vector3[] directions = { Vector3.right, Vector3.left, Vector3.up, Vector3.down };
            foreach (Vector3 dir in directions)
            {
                Vector3 targetWorldPos = barcoObj.transform.position + (dir * cachedCellSize);
                Tile tile = GetTileAtWorldPosition(targetWorldPos);

                if (tile != null && tile.metadata.camada == 1)
                {
                    boatMov.isOnWater = false;
                    GameState.IsOnWater = boatMov.isOnWater;
                    capitãoObj.SetActive(true);
                    capitãoObj.transform.position = targetWorldPos;
                    camera.orthographicSize = Mathf.Lerp(3.5f, 5f, Time.deltaTime * 0.5f);

                    this.player = capitãoObj.transform;
                    barcoObj.GetComponent<Rigidbody2D>().linearVelocity = Vector2.zero;
                    AtualizarReferenciaNasChunks();
                    return;
                }
            }
        }
        else
        {
            float distanciaProBarco = Vector3.Distance(capitãoObj.transform.position, barcoObj.transform.position);
            if (distanciaProBarco < cachedCellSize * 1.5f)
            {
                boatMov.isOnWater = true;
                GameState.IsOnWater = boatMov.isOnWater;
                capitãoObj.SetActive(false);
                camera.orthographicSize = Mathf.Lerp(5f, 3.5f, Time.deltaTime * 0.5f);
                this.player = barcoObj.transform;
                AtualizarReferenciaNasChunks();
                return;
            }
        }
    }

    private void AtualizarReferenciaNasChunks()
    {
        foreach (var mg in activeChunks.Values)
            mg.Setup(this.player.gameObject, this);
    }

    public void TryFindWaterTile()
    {
        if (player == null) return;

        Vector3 startPos = player.position;
        int searchRadius = 5;

        for (int x = -searchRadius; x <= searchRadius; x++)
        {
            for (int y = -searchRadius; y <= searchRadius; y++)
            {
                Vector3 checkPos = startPos + new Vector3(x * cachedCellSize, y * cachedCellSize, 0);
                Tile tile = GetTileAtWorldPosition(checkPos);

                if (tile != null && tile.metadata.camada == 0)
                {
                    player.position = checkPos;
                    Debug.Log("Jogador movido para a água!");
                    return;
                }
            }
        }
        Debug.LogWarning("Nenhum tile de água encontrado por perto.");
    }
}
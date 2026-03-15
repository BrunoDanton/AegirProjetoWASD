using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class MapGenerator : MonoBehaviour
{
    public UnityEngine.Tilemaps.Tilemap tilemap;
    public TilesetData tilesetData;
    public RuleManager ruleManager;
    public Vector2Int chunkSize;

    [Tooltip("Quantos colapsos são feitos por frame durante a geração assíncrona.")]
    public int collapsesPerFrame = 10;
    private Cell[,] cells;
    private int GridW => chunkSize.x + 2;
    private int GridH => chunkSize.y + 2;
    private int TileCount => tilesetData.tileset.Count;

    // Compatible funciona como uma tabela de consulta rápida (índice a, indice b, direção da conexão)
    // Usado para evitar acessar o ruleManager com frequência (custoso)
    private bool[,,] compatible;

    private readonly Vector2Int[] directions = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
    private readonly int[] dirIndices = { 0, 1, 2, 3 };

    // Estado do halo salvo para reiniciar sem recalcular do WorldGenerator (Quais são as células vizinhas já definidas?) 
    private BitArray[,] haloSnapshot;

    public bool IsGenerating { get; private set; } = false;
    public bool GenerationSucceeded { get; private set; } = false;

    public System.Action<MapGenerator, bool> OnGenerationComplete;
    public WorldGenerator worldGenerator;
    public GameObject player;

    // Ciclo de Vida ---------------------------------------------------------------------------------------------------------------------------------------------

    void Awake()
    {
        ruleManager = FindFirstObjectByType<RuleManager>();
    }

    // Métodos Públicos-------------------------------------------------------------------------------------------------------------------------------------------
    public void Setup(GameObject player, WorldGenerator worldGenerator)
    {
        this.player = player;
        this.worldGenerator = worldGenerator;

        NPCsMovement[] npcsNoMapa = GetComponentsInChildren<NPCsMovement>();
        foreach (var npc in npcsNoMapa)
        {
            npc.Setup(player, worldGenerator);
        }
    }
    // Geração síncrona — bloqueia o jogo até carregar a chunk (usado no campo de visão inicial)
    public bool GenerateChunk(Dictionary<Vector2Int, Tile> borderTiles, float islandNoise)
    {
        int maxRestarts = 10; // Utilizo maxRestarts para que, caso haja uma contradição, seja reiniciada a geração da chunk
        for (int attempt = 0; attempt < maxRestarts; attempt++)
        {
            InitCells(borderTiles); // Inicializa todas as células (caso haja alguma chunk vizinha já feita, passa as células colapsadas das bordas para o algorítmo)

            if (RunCollapseSync(islandNoise)) // Escolhe, colapsa e propaga as consequências para cada célula da chunk, se for bem sucedido, retorna "true"
            {
                GenerationSucceeded = true;
                RenderMap();
                SpawnEntities();
                return true;
            }
        }
        GenerationSucceeded = false;
        return GenerationSucceeded;
    }

    // Geração assíncrona — distribui o trabalho em frames (gera a chunk enquanto o jogo acontece)
    public void GenerateChunkAsync(Dictionary<Vector2Int, Tile> borderTiles, float islandNoise)
    {
        StartCoroutine(GenerateChunkCoroutine(borderTiles, islandNoise)); 
    }

    // Atualiza o Halo (contorno de tiles vizinhos) e atualiza os tiles interiores
    public void UpdateHaloAndRepropagate(Dictionary<Vector2Int, Tile> newHaloTiles)
    {
        if (cells == null) return;
        EnsureCompatibilityCache(); // Garante que seja criada somente uma vez e que esteja pronta para o uso antes de qualquer cálculo

        foreach (var keyValue in newHaloTiles)
        {
            if (!IsInsideBounds(keyValue.Key)) continue;

            // Inicializa cada célula do halo
            Cell haloCell = cells[keyValue.Key.x, keyValue.Key.y];
            if (haloCell.isCollapsed()) continue;

            // Define o tile da célula da borda e propaga a informação para os vizinhos de dentro da chunk
            int tileIndex = tilesetData.tileset.IndexOf(keyValue.Value);
            if (tileIndex < 0) continue;
            haloCell.CollapseCell(tileIndex);
            PropagateConsequences(haloCell);
        }
        RenderMap();
    }

    // Transforma as células/tiles da chunk em uma lista de bytes
    public byte[] GetChunkData()
    {
        if (cells == null) return null;

        // Define o tamanho do array
        byte[] data = new byte[chunkSize.x * chunkSize.y];
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {

                // Utilizamos x + 1 e y + 1 para obter a coordenada correta (sem as bordas das outras chunks)
                Cell cell = cells[x + 1, y + 1];
                int tileIndex = cell.CollapsedIndex();
                data[x * chunkSize.y + y] = tileIndex >= 0 ? (byte)tileIndex : (byte)0;
            }
        }
        return data;
    }

    // Carrega uma chunk já criada a partir dos dados gerados pela função anterior (GetChunkData)
    public void LoadFromData(byte[] data)
    {
        cells = new Cell[GridW, GridH];
        for (int x = 0; x < GridW; x++)
            for (int y = 0; y < GridH; y++)
                cells[x, y] = new Cell(TileCount, new Vector2Int(x, y));

        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                int tileIndex = data[x * chunkSize.y + y];
                cells[x + 1, y + 1].CollapseCell(tileIndex);
            }
        }


        RenderMap();
    }

    // Lógica Interna e Corrotinas ---------------------------------------------------------------------------------------------------------------------------------------------

    private IEnumerator GenerateChunkCoroutine(Dictionary<Vector2Int, Tile> borderTiles, float islandNoise)
    {
        IsGenerating = true;
        GenerationSucceeded = false;

        // Mesma lógica do GenerateChunk
        int maxRestarts = 10;
        for (int attempt = 0; attempt < maxRestarts; attempt++)
        {
            InitCells(borderTiles);

            bool success = false;
            yield return RunCollapseAsync(islandNoise, result => success = result);

            if (success)
            {
                GenerationSucceeded = true;
                RenderMap();
                break;
            }
        }

        IsGenerating = false;
        OnGenerationComplete?.Invoke(this, GenerationSucceeded);
    }

    // Inicializa as células e passa as informações dos vizinhos
    private void InitCells(Dictionary<Vector2Int, Tile> borderTiles)
    {
        EnsureCompatibilityCache(); // Garante que seja criada somente uma vez e que esteja pronta para o uso antes de qualquer cálculo

        cells = new Cell[GridW, GridH]; 

        // Inicializa cada célula
        for (int x = 0; x < GridW; x++)
            for (int y = 0; y < GridH; y++)
                cells[x, y] = new Cell(TileCount, new Vector2Int(x, y));

        // Se houverem tiles nas bordas vizinhas:
        if (borderTiles != null)
        {
            // Separo em dois laços para somente propagar as consequencias depois que todas as células estiverem colapsadas
            // O motivo: Evitar contradições
            foreach (var keyValue in borderTiles)
            {
                if (!IsInsideBounds(keyValue.Key)) continue;
                int tileIndex = tilesetData.tileset.IndexOf(keyValue.Value);
                if (tileIndex >= 0) cells[keyValue.Key.x, keyValue.Key.y].CollapseCell(tileIndex);
            }
            foreach (var keyValue in borderTiles)
            {
                if (!IsInsideBounds(keyValue.Key)) continue;
                PropagateConsequences(cells[keyValue.Key.x, keyValue.Key.y]);
            }
        }

        // Salva o estado pós-halo para reinício rápido
        haloSnapshot = new BitArray[GridW, GridH];
        for (int x = 0; x < GridW; x++)
            for (int y = 0; y < GridH; y++)
                haloSnapshot[x, y] = new BitArray(cells[x, y].possible);
    }

    // Executa o laço de preenchimento das chunks iniciais
    private bool RunCollapseSync(float noise)
    {
        int totalReal = chunkSize.x * chunkSize.y;
        int colapsadas = 0;
        int maxAttempts = totalReal * 3;
        int attempts = 0;

        while (colapsadas < totalReal && attempts < maxAttempts)
        {
            Cell chosen = ChooseCell();
            if (chosen == null) break;

            CollapseAndPropagate(chosen, noise);

            if (HasContradiction())
            {
                RestartFromHalo();
                colapsadas = 0;
                attempts++;
                continue;
            }
            colapsadas++;
        }

        return !HasContradiction();
    }

    private IEnumerator RunCollapseAsync(float noise, System.Action<bool> onDone)
    {
        int totalReal = chunkSize.x * chunkSize.y;
        int colapsadas = 0;
        int maxAttempts = totalReal * 3;
        int attempts = 0;
        int collapsesThisFrame = 0;

        while (colapsadas < totalReal && attempts < maxAttempts)
        {
            Cell chosen = ChooseCell();
            if (chosen == null) break;

            CollapseAndPropagate(chosen, noise);

            if (HasContradiction())
            {
                RestartFromHalo();
                colapsadas = 0;
                attempts++;
            }
            else
            {
                colapsadas++;
                RenderMap();
            }

            collapsesThisFrame++;
            if (collapsesThisFrame >= collapsesPerFrame)
            {
                collapsesThisFrame = 0;
                yield return null;
            }
        }

        onDone(!HasContradiction());
    }

    private Cell ChooseCell()
    {
        int min = int.MaxValue;
        List<Cell> possibleCells = new List<Cell>();

        for (int x = 1; x <= chunkSize.x; x++)
        {
            for (int y = 1; y <= chunkSize.y; y++)
            {
                Cell c = cells[x, y];
                if (c.isCollapsed()) continue;
                int count = c.CountPossible();
                if (count == 0) continue;
                if (count < min) { min = count; possibleCells.Clear(); possibleCells.Add(c); }
                else if (count == min) possibleCells.Add(c);
            }
        }

        return possibleCells.Count > 0 ? possibleCells[Random.Range(0, possibleCells.Count)] : null;
    }

    private void CollapseAndPropagate(Cell cell, float noise)
    {
        // Calcula peso total iterando diretamente sobre bits — sem alocação
        float pesoTotal = 0;
        for (int i = 0; i < TileCount; i++)
        {
            if (!cell.possible[i]) continue;
            Tile tile = tilesetData.tileset[i]; // Obtém o tile atual 

            // Força a geração de ilhas onde o perlinNoise definir um ruido alto
            pesoTotal += (tile.metadata.camada % 2 == 0 && tile.metadata.camada != 0) ? tile.peso * (noise * 10) : tile.peso;
        }

        float randomNumber = Random.Range(0, pesoTotal);
        int chosen = -1;

        for (int i = 0; i < TileCount; i++)
        {
            if (!cell.possible[i]) continue;

            Tile tile = tilesetData.tileset[i];
            randomNumber -= (tile.metadata.camada % 2 == 0 && tile.metadata.camada != 0) ? tile.peso * (noise * 10) : tile.peso;
        
            if (randomNumber <= 0) { chosen = i; break; }
        }

        // Fallback: último bit ativo
        if (chosen < 0)
            for (int i = TileCount - 1; i >= 0; i--)
                if (cell.possible[i]) { chosen = i; break; }

        cell.CollapseCell(chosen);
        PropagateConsequences(cell);
    }

    private void PropagateConsequences(Cell start)
    {
        Queue<Cell> queue = new Queue<Cell>();

        // Enquanto houverem células com mudançs a serem propagadas, propaga as consequências 
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            Cell currentCell = queue.Dequeue();

            for (int d = 0; d < 4; d++)
            {
                Vector2Int neighbornPos = currentCell.coordinates + directions[d];

                if (!IsInsideBounds(neighbornPos)) continue;

                Cell neighborn = cells[neighbornPos.x, neighbornPos.y];
                if (neighborn.isCollapsed()) continue;

                bool hasChanged = false;

                // Para cada tile candidato do vizinho
                for (int ni = 0; ni < TileCount; ni++)
                {
                    if (!neighborn.possible[ni]) continue;

                    // Verifica se pelo menos um tile de currentCell é compatível com neighbornIndex (ni) na direção d
                    bool hasSupport = false;
                    for (int ci = 0; ci < TileCount; ci++)
                    {
                        if (!currentCell.possible[ci]) continue;
                        if (compatible[ci, ni, d]) { hasSupport = true; break; }
                    }

                    if (!hasSupport)
                    {
                        neighborn.possible[ni] = false;
                        hasChanged = true;
                    }
                }

                if (hasChanged) queue.Enqueue(neighborn);
            }
        }
    }

    // Auxiliares -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    
    // Obtém o tile da célula colapsada na posição (x,y)
    public Tile GetTileAt(int x, int y)
    {
        if (cells == null) return null;

        Cell c = cells[x + 1, y + 1];
        
        if (c.isEmpty()) return null;
        int tileIndex = c.CollapsedIndex();
        
        return tileIndex >= 0 ? tilesetData.tileset[tileIndex] : null;
    }

    public void InstantiateCreature(Cell cell, int x, int y, ref int spawnCount)
    {
        int tileIndex = cell.CollapsedIndex();
        Vector3 basePosition = tilemap.GetCellCenterWorld(new Vector3Int(x - 1, y - 1, 0));
        Tile tile = tilesetData.tileset[tileIndex];

        for (int i = 0; i < tile.spawnableCreatures.Count; i++)
        {
            if (Random.value <= tile.spawnableCreatures[i].spawnChance)
            {
                for (int j = 0; j < tile.spawnableCreatures[i].quantity; j++)
                {
                    Vector3 finalPosition = basePosition + new Vector3(Random.Range(-0.2f, 0.2f), Random.Range(-0.2f, 0.2f), 0);
                    
                    GameObject creatureGO = Instantiate(tile.spawnableCreatures[i].creature, finalPosition, Quaternion.identity);

                    NPCsMovement mov = creatureGO.GetComponent<NPCsMovement>();

                    if (mov != null)
                    {
                        mov.Setup(this.player, this.worldGenerator);
                    }

                    spawnCount++;
                }
            }
        }
    }
    private void EnsureCompatibilityCache()
    {
        if (compatible != null) return;
        BuildCompatibilityCache();
    }

    private void BuildCompatibilityCache()
    {
        int n = TileCount;
        compatible = new bool[n, n, 4];

        for (int a = 0; a < n; a++)
        {
            for (int b = 0; b < n; b++)
            {
                Tile tA = tilesetData.tileset[a];
                Tile tB = tilesetData.tileset[b];
                compatible[a, b, 0] = !ruleManager.IsBlocked(tA, tB, Vector2Int.up);
                compatible[a, b, 1] = !ruleManager.IsBlocked(tA, tB, Vector2Int.down);
                compatible[a, b, 2] = !ruleManager.IsBlocked(tA, tB, Vector2Int.left);
                compatible[a, b, 3] = !ruleManager.IsBlocked(tA, tB, Vector2Int.right);
            }
        }
    }

    private void RestartFromHalo()
    {
        for (int x = 0; x < GridW; x++)
            for (int y = 0; y < GridH; y++)
                cells[x, y].possible = new BitArray(haloSnapshot[x, y]);
    }

    private bool IsInsideBounds(Vector2Int p) => p.x >= 0 && p.x < GridW && p.y >= 0 && p.y < GridH;

    private bool HasContradiction()
    {
        for (int x = 1; x <= chunkSize.x; x++)
            for (int y = 1; y <= chunkSize.y; y++)
                if (cells[x, y].isEmpty())
                    return true;
        return false;
    }

    public void ForceWaterChunk(Dictionary<Vector2Int, Tile> borderTiles)
    {
        InitCells(borderTiles);

        int waterIdx = tilesetData.tileset.FindIndex(t => t.metadata.camada == 0);
        if (waterIdx == -1) return;

        for (int x = 0; x < GridW; x++)
            for (int y = 0; y < GridH; y++)
                cells[x, y].CollapseCell(waterIdx);

        RenderMap();
    }

    private void RenderMap()
    {
        for (int x = 1; x <= chunkSize.x; x++)
        {
            for (int y = 1; y <= chunkSize.y; y++)
            {
                if (cells[x, y].isCollapsed())
                {
                    int tileIndex = cells[x, y].CollapsedIndex();
                    tilemap.SetTile(new Vector3Int(x - 1, y - 1, 0), tilesetData.tileset[tileIndex].tilemapTile);
                }
            }
        }
    }

    [Header("Spawn Settings")]
    public int maxCreaturesPerChunk = 10;

    public void SpawnEntities()
    {
        int totalSpawned = 0;

        for (int x = 1; x <= chunkSize.x; x++)
        {
            for (int y = 1; y <= chunkSize.y; y++)
            {
                if (totalSpawned >= maxCreaturesPerChunk) return;

                if (cells[x, y].isCollapsed())
                {
                    int before = totalSpawned;
                    InstantiateCreature(cells[x, y], x, y, ref totalSpawned);
                }
            }
        }
    }
}
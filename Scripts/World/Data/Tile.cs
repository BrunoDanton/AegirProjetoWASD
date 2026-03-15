using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "New Tile", menuName = "WFC/Tile")]
public class Tile : ScriptableObject
{
    public enum Type : byte { Bloco, Costa, Quina, QuinaInterna }
    public enum Directions { N, S, O, L, NL, NO, SL, SO, None }

    [Serializable]
    public struct CornerSockets
    {
        public int NO, NE, SO, SE;
    }

    [Serializable]
    public struct SpawnableCreatures
    {
        public GameObject creature;
        public int quantity;
        [Range(0,1f)] public float spawnChance;
    }

    [Serializable]
    public struct TileMetadata
    {
        public int camada; // 0: Água, 1: Costa, 2: Terra
        public Type type;
        public Directions direction;
        [HideInInspector] public CornerSockets corners;
    }

    public UnityEngine.Tilemaps.TileBase tilemapTile;
    public float peso = 1f;
    public List<SpawnableCreatures> spawnableCreatures;
    public TileMetadata metadata;

    private void OnValidate()
    {
        metadata.corners = GerarCorners(metadata.camada, metadata.type, metadata.direction);
    }

    private CornerSockets GerarCorners(int a, Type type, Directions d)
    {
        int i = a - 1; // camada inferior (ex: água)
        int s = a + 1; // camada superior (ex: terra)

        if (type == Type.Bloco)
            return new CornerSockets { NO = a, NE = a, SO = a, SE = a };

        return (type, d) switch
        {
            // Costa: uma borda inteira é água, a oposta é terra
            (Type.Costa, Directions.N)  => new CornerSockets { NO = i, NE = i, SO = s, SE = s },
            (Type.Costa, Directions.S)  => new CornerSockets { NO = s, NE = s, SO = i, SE = i },
            (Type.Costa, Directions.L)  => new CornerSockets { NO = s, NE = i, SO = s, SE = i },
            (Type.Costa, Directions.O)  => new CornerSockets { NO = i, NE = s, SO = i, SE = s },

            // Quina convexa: cercada mais por água — três cantos de agua
            (Type.Quina, Directions.NL) => new CornerSockets { NO = i, NE = i, SO = s, SE = i },
            (Type.Quina, Directions.NO) => new CornerSockets { NO = i, NE = i, SO = i, SE = s },
            (Type.Quina, Directions.SL) => new CornerSockets { NO = s, NE = i, SO = i, SE = i },
            (Type.Quina, Directions.SO) => new CornerSockets { NO = i, NE = s, SO = i, SE = i },

            // QuinaInterna côncava: cercada mais por terra — apenas um canto é agua
            (Type.QuinaInterna, Directions.NL) => new CornerSockets { NO = s, NE = s, SO = i, SE = s },
            (Type.QuinaInterna, Directions.NO) => new CornerSockets { NO = s, NE = s, SO = s, SE = i },
            (Type.QuinaInterna, Directions.SL) => new CornerSockets { NO = i, NE = s, SO = s, SE = s },
            (Type.QuinaInterna, Directions.SO) => new CornerSockets { NO = s, NE = i, SO = s, SE = s },

            _ => new CornerSockets()
        };
    }

    /// <summary>
    /// Verifica se este tile é compatível com um vizinho em uma dada direção,
    /// comparando os cantos compartilhados entre os dois tiles.
    /// </summary>
    public bool IsCompatibleWith(Tile neighbor, Vector2Int direction)
    {
        CornerSockets a = metadata.corners;
        CornerSockets b = neighbor.metadata.corners;

        // Vizinho à direita: borda leste de A encosta na borda oeste de B
        // Cantos compartilhados: A.NE == B.NO  e  A.SE == B.SO
        if (direction == Vector2Int.right)
            return a.NE == b.NO && a.SE == b.SO;

        // Vizinho à esquerda: borda oeste de A encosta na borda leste de B
        // Cantos compartilhados: A.NO == B.NE  e  A.SO == B.SE
        if (direction == Vector2Int.left)
            return a.NO == b.NE && a.SO == b.SE;

        // Vizinho acima: borda norte de A encosta na borda sul de B
        // Cantos compartilhados: A.NO == B.SO  e  A.NE == B.SE
        if (direction == Vector2Int.up)
            return a.NO == b.SO && a.NE == b.SE;

        // Vizinho abaixo: borda sul de A encosta na borda norte de B
        // Cantos compartilhados: A.SO == B.NO  e  A.SE == B.NE
        if (direction == Vector2Int.down)
            return a.SO == b.NO && a.SE == b.NE;

        return false;
    }
}
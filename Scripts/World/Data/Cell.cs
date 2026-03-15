using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Cell
{
    public BitArray possible;       // Um bit por tile do tileset — 1 = possível, 0 = eliminado
    public Vector2Int coordinates;

    private int tileCount;          // Cache do tileset.Count para não referenciar a lista a todo momento

    public Cell(int tileCount, Vector2Int coords)
    {
        this.tileCount = tileCount;
        this.coordinates = coords;
        possible = new BitArray(tileCount, true); // Começa com todos possíveis
    }

    public bool isCollapsed() => CountPossible() == 1;

    public bool isEmpty() => CountPossible() == 0;

    public int CountPossible()
    {
        int count = 0;
        for (int i = 0; i < possible.Count; i++)
            if (possible[i]) count++;
        return count;
    }

    // Retorna o índice do único tile possível (-1 se não colapsado)
    public int CollapsedIndex()
    {
        for (int i = 0; i < possible.Count; i++)
            if (possible[i]) return i;
        return -1;
    }

    public void CollapseCell(int tileIndex)
    {
        possible.SetAll(false);
        possible[tileIndex] = true;
    }

    // Retorna todos os índices ainda possíveis
    public List<int> PossibleIndices()
    {
        var result = new List<int>();
        for (int i = 0; i < possible.Count; i++)
            if (possible[i]) result.Add(i);
        return result;
    }

    // Copia o estado de outro BitArray para este (usado no reset)
    public void CopyFrom(BitArray other)
    {
        possible = new BitArray(other);
    }
}
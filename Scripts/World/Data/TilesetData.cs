using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "TilesetData", menuName = "Scriptable Objects/TilesetData")]
public class TilesetData : ScriptableObject
{
    public List<Tile> tileset;
    
}

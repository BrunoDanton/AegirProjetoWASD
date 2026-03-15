using System.Collections.Generic;
using System;
using UnityEngine;

[CreateAssetMenu(fileName = "StructureData", menuName = "Scriptable Objects/StructureData")]
public class StructureData : ScriptableObject
{
    public string structureName;
    public Vector2Int structureDimensions;
    public GameObject structurePrefab;
    [Range(0.0f, 1.0f)] public float spawnChance;
    public List<int> validBaseLayers;
    public float raioDeIsolamento;

    [Serializable]
    public struct LayerOverride
    {
        public List<Vector2Int> localCoordinates;
        public int layer;
    }
    public List<LayerOverride> layerOverrides;
}

using System;
using System.Collections.Generic;
using UnityEngine;

public class RuleManager : MonoBehaviour
{
    [Serializable]
    public struct TileIdentifier
    {
        public Tile.Type tipo;
        public Tile.Directions direcao;
    }

    [Serializable]
    public class TileRule // Transformado em classe para busca segura de null
    {
        public TileIdentifier origem;
        public List<TileIdentifier> bloqueadosAcima = new List<TileIdentifier>();
        public List<TileIdentifier> bloqueadosAbaixo = new List<TileIdentifier>();
        public List<TileIdentifier> bloqueadosEsquerda = new List<TileIdentifier>();
        public List<TileIdentifier> bloqueadosDireita = new List<TileIdentifier>();
    }

    public List<TileRule> regrasDeBloqueio;
    public TilesetData tilesetData;

    private Dictionary<Tile, HashSet<Tile>[]> fastRules;

    private void Awake()
    {
        ProcessRules();
    }

    private void ProcessRules()
    {
        List<TileRule> regrasEspelhadas = new List<TileRule>();
        var originais = regrasDeBloqueio.ToArray();

        foreach (var regra in originais)
        {
            AdicionarEspelho(regrasEspelhadas, regra.origem, regra.bloqueadosAcima, "abaixo");
            AdicionarEspelho(regrasEspelhadas, regra.origem, regra.bloqueadosAbaixo, "acima");
            AdicionarEspelho(regrasEspelhadas, regra.origem, regra.bloqueadosEsquerda, "direita");
            AdicionarEspelho(regrasEspelhadas, regra.origem, regra.bloqueadosDireita, "esquerda");
        }
        regrasDeBloqueio.AddRange(regrasEspelhadas);

        fastRules = new Dictionary<Tile, HashSet<Tile>[]>();
        foreach (var regra in regrasDeBloqueio)
        {
            Tile tileOrigem = FindTile(regra.origem);
            if (tileOrigem == null) continue;

            if (!fastRules.ContainsKey(tileOrigem))
                fastRules[tileOrigem] = new HashSet<Tile>[4] { new HashSet<Tile>(), new HashSet<Tile>(), new HashSet<Tile>(), new HashSet<Tile>() };

            FillSet(fastRules[tileOrigem][0], regra.bloqueadosAcima);
            FillSet(fastRules[tileOrigem][1], regra.bloqueadosAbaixo);
            FillSet(fastRules[tileOrigem][2], regra.bloqueadosEsquerda);
            FillSet(fastRules[tileOrigem][3], regra.bloqueadosDireita);
        }
    }

    private void AdicionarEspelho(List<TileRule> listaEspelhada, TileIdentifier origem, List<TileIdentifier> bloqueados, string dirInv)
    {
        if (bloqueados == null) return;
        foreach (var bloqueado in bloqueados)
        {
            if (ExisteNasOriginais(bloqueado, origem, dirInv)) continue;

            TileRule alvo = listaEspelhada.Find(r => r.origem.tipo == bloqueado.tipo && r.origem.direcao == bloqueado.direcao);
            if (alvo == null)
            {
                alvo = new TileRule { origem = bloqueado };
                listaEspelhada.Add(alvo);
            }

            var listaDestino = dirInv switch { "acima" => alvo.bloqueadosAcima, "abaixo" => alvo.bloqueadosAbaixo, "esquerda" => alvo.bloqueadosEsquerda, "direita" => alvo.bloqueadosDireita, _ => null };
            if (listaDestino != null && !listaDestino.Exists(b => b.tipo == origem.tipo && b.direcao == origem.direcao))
                listaDestino.Add(origem);
        }
    }

    private bool ExisteNasOriginais(TileIdentifier de, TileIdentifier bloqueia, string dir)
    {
        return regrasDeBloqueio.Exists(r => r.origem.tipo == de.tipo && r.origem.direcao == de.direcao && 
               ObterLista(r, dir).Exists(b => b.tipo == bloqueia.tipo && b.direcao == bloqueia.direcao));
    }

    private List<TileIdentifier> ObterLista(TileRule r, string dir) => dir switch { "acima" => r.bloqueadosAcima, "abaixo" => r.bloqueadosAbaixo, "esquerda" => r.bloqueadosEsquerda, "direita" => r.bloqueadosDireita, _ => new List<TileIdentifier>() };

    private void FillSet(HashSet<Tile> set, List<TileIdentifier> ids) { foreach (var id in ids) { Tile t = FindTile(id); if (t != null) set.Add(t); } }

    private Tile FindTile(TileIdentifier id) => tilesetData.tileset.Find(t => t.metadata.type == id.tipo && t.metadata.direction == id.direcao);

    public bool IsBlocked(Tile current, Tile neighbor, Vector2Int direction)
    {
        bool compatible = current.IsCompatibleWith(neighbor, direction);

        if (!current.IsCompatibleWith(neighbor, direction)) return true; //

        if (fastRules.TryGetValue(current, out var dirs))
        {
            int idx = direction == Vector2Int.up ? 0 : direction == Vector2Int.down ? 1 : direction == Vector2Int.left ? 2 : 3;
            return dirs[idx].Contains(neighbor);
        }
        return false;
    }
}
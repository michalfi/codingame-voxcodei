using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VoxCodei
{
    public class Program
    {
        static void Main(string[] args)
        {
            var player = new Player(Console.In, Console.Out, Console.Error, new BruteStrategy(Console.Error));

            player.Init();

            while (true)
            {
                player.Act();
            }
        }
    }

    public interface IStrategy
    {
        IEnumerable<Bomb> PlanActions(Grid grid, int rounds, int bombCount);
    }

    public class GreedyStrategy : IStrategy
    {
        public IEnumerable<Bomb> PlanActions(Grid grid, int rounds, int bombCount)
        {
            var planGrid = new Grid(grid);
            var clearedNodes = new bool[grid.Width, grid.Height];
            var bombsUsed = 0;

            for (int roundNumber = 0; roundNumber < rounds; roundNumber++)
            {
                planGrid.Tick();
                if (bombsUsed == bombCount)
                {
                    yield return null;
                    continue;
                }

                var positions = Enumerable.Range(0, grid.Width)
                    .SelectMany(col => Enumerable.Range(0, grid.Height).Select(row => new {col, row}))
                    .Where(pos => planGrid.GetCell(pos.col, pos.row) == CellContents.Empty);

                var effect = positions.Select(pos => new
                {
                    pos.col,
                    pos.row,
                    nodes = CountBlastedNodes(pos.col, pos.row, planGrid, clearedNodes)
                });

                var best = effect.OrderByDescending(x => x.nodes).First();
                if (best.nodes == 0)
                {
                    yield return null;
                    continue;
                }

                yield return new Bomb(best.col, best.row);
                planGrid.VisitBlast(best.col, best.row, (col, row, contents) => { clearedNodes[col, row] = true; });
                planGrid.PutBomb(best.col, best.row);
                bombsUsed++;
            }
        }

        private int CountBlastedNodes(int col, int row, Grid grid, bool[,] clearedNodes)
        {
            var count = 0;
            grid.VisitBlast(col, row, (c, r, contents) =>
            {
                if (contents == CellContents.Node && !clearedNodes[c, r])
                    count++;
            });
            return count;
        }
    }

    public class BruteStrategy : IStrategy
    {
        public BruteStrategy(TextWriter debug)
        {
            Debug = debug;
        }

        private TextWriter Debug { get; }

        public IEnumerable<Bomb> PlanActions(Grid grid, int rounds, int bombCount)
        {
            var potentials = Enumerable.Range(0, grid.Width)
                .SelectMany(col => Enumerable.Range(0, grid.Height).Select(row => new {col, row}))
                .Where(pos => grid.GetCell(pos.col, pos.row) != CellContents.Passive)
                .Select(pos =>
                    new PotentialBomb(pos.col, pos.row,
                        grid.EnumerateBlast(pos.col, pos.row).Where(blasted =>
                            grid.GetCell(blasted.Col, blasted.Row) == CellContents.Node))).ToArray();
            var simplified = potentials.Where(b => grid.GetCell(b.Position.Col, b.Position.Row) == CellContents.Empty)
                .GroupBy(b => b.BlastString()).Select(g => g.First());
            var interesting = potentials.Where(b => grid.GetCell(b.Position.Col, b.Position.Row) == CellContents.Node)
                .Concat(simplified).ToArray();

            var plan = Plan(grid, grid, rounds + 1, bombCount, interesting, true);
            if (plan == null)
            {
                Debug.WriteLine("Failed");
                return Enumerable.Repeat<Bomb>(null, rounds);
            }

            return plan.Concat(Enumerable.Repeat<Bomb>(null, rounds - plan.Length));
        }

        public Bomb[] Plan(Grid simulation, Grid result, int rounds, int bombCount, PotentialBomb[] blasts, bool afterBomb)
        {
            var exploded = new List<Coords>();
            simulation.Tick((col, row) => exploded.Add(new Coords(col, row)));

            if (result.Count(CellContents.Node) == 0)
                return new Bomb[0];

            if (bombCount <= 0 || rounds <= 0)
                return null;

            var triedBlasts = afterBomb
                ? blasts.Where(b => simulation.GetCell(b.Position.Col, b.Position.Row) == CellContents.Empty)
                : blasts.Where(b => exploded.Contains(b.Position));

            var options = triedBlasts
                .Select(b => new {pos = b.Position, effect = b.Blast.Count(blast => result.GetCell(blast.Col, blast.Row) == CellContents.Node) })
                .Where(b => b.effect > 0)
                .OrderByDescending(b => b.effect)
                .Select(b => b.pos).ToArray();

            foreach (var option in options)
            {
                var updatedSimulation = new Grid(simulation);
                updatedSimulation.PutBomb(option.Col, option.Row);
                var updatedResult = new Grid(result);
                updatedResult.VisitBlast(option.Col, option.Row, (col, row, contents) => updatedResult.Clear(col, row));

                var plan = Plan(updatedSimulation, updatedResult, rounds - 1, bombCount - 1, blasts, true);
                if (plan != null)
                    return new[] {new Bomb(option.Col, option.Row)}.Concat(plan).ToArray();
            }

            if (simulation.GetBombCount() > 0)
            {
                var waitSimulation = new Grid(simulation);
                var waitPlan = Plan(waitSimulation, result, rounds - 1, bombCount, blasts, false);
                if (waitPlan != null)
                    return new Bomb[] {null}.Concat(waitPlan).ToArray();
            }

            return null;
        }
    }

    public class GuessingStrategy : IStrategy
    {
        public IEnumerable<Bomb> PlanActions(Grid grid, int rounds, int bombCount)
        {
            var potentials = Enumerable.Range(0, grid.Width)
                .SelectMany(col => Enumerable.Range(0, grid.Height).Select(row => new {col, row}))
                .Where(pos => grid.GetCell(pos.col, pos.row) != CellContents.Passive).Select(pos =>
                    new PotentialBomb(pos.col, pos.row,
                        grid.EnumerateBlast(pos.col, pos.row).Where(blasted =>
                            grid.GetCell(blasted.Col, blasted.Row) == CellContents.Node))).ToList();

            var nodeCount = grid.Count(CellContents.Node);
            var set = new BombSet();
            while (nodeCount > set.ClearedCount)
            {
                var chosen = potentials.OrderByDescending(bomb => set.Diff(bomb))
                    .ThenByDescending(bomb => bomb.Blast.Length).First();
                potentials.Remove(chosen);
                set.Add(chosen);
                set.Simplify();
            }

            var bombs = set.Bombs.ToArray();
            Console.Error.WriteLine("Set found: " + string.Join(", ", bombs.Select(b => $"{b.Col}:{b.Row}")));
            var orderer = new BombOrderer(grid);

            var order = orderer.Order(bombs).ToArray();
            return order.Concat(Enumerable.Repeat<Bomb>(null, Math.Max(rounds - order.Length, 1)));
        }

        private class BombSet
        {
            public void Add(PotentialBomb bomb)
            {
                BombMap.Add(bomb.Position, bomb);
                foreach (var cleared in bomb.Blast)
                {
                    if (!ClearedNodes.TryGetValue(cleared, out var coverCount))
                        coverCount = 0;
                    ClearedNodes[cleared] = coverCount + 1;
                }
            }

            public int Diff(PotentialBomb bomb)
            {
                return bomb.Blast.Count(pos => !ClearedNodes.ContainsKey(pos));
            }

            public void Simplify()
            {
                foreach (var bomb in BombMap.Values)
                {
                    var minCover = bomb.Blast.Min(pos => ClearedNodes[pos]);
                    if (minCover > 1)
                    {
                        foreach (var cleared in bomb.Blast)
                            ClearedNodes[cleared] -= 1;
                        BombMap.Remove(bomb.Position);
                    }
                }
            }

            public IEnumerable<Coords> Bombs => BombMap.Keys;

            public int ClearedCount => ClearedNodes.Count;

            private Dictionary<Coords, PotentialBomb> BombMap { get; } = new Dictionary<Coords, PotentialBomb>();

            private Dictionary<Coords, int> ClearedNodes { get; } = new Dictionary<Coords, int>();
        }
    }

    public class PotentialBomb
    {
        public PotentialBomb(int col, int row, IEnumerable<Coords> blast)
        {
            Position = new Coords(col, row);
            Blast = blast.ToArray();
        }

        public Coords Position { get; }

        public Coords[] Blast { get; }

        public string BlastString()
        {
            return string.Join(", ", Blast.OrderBy(b => b.Col).ThenBy(b => b.Row));
        }
    }

    public class BombOrderer
    {
        public BombOrderer(Grid grid)
        {
            Grid = grid;
        }

        private Grid Grid { get; }

        public IEnumerable<Bomb> Order(IEnumerable<Coords> bombPositions)
        {
            var remaining = bombPositions.ToList();
            var levels = remaining.ToDictionary(b => b, b => int.MaxValue);
            var clearers = new Dictionary<Coords, Coords>();
            var g = new Grid(Grid);

            while (remaining.Count > 0)
            {
                var possible = remaining.Where(pos => g.GetCell(pos.Col, pos.Row) == CellContents.Empty).ToArray();
                foreach (var pos in possible)
                {
                    foreach (var cleared in g.EnumerateBlast(pos.Col, pos.Row))
                    {
                        if (remaining.Contains(cleared))
                            clearers[cleared] = pos;
                        g.Clear(pos.Col, pos.Row);
                    }

                    remaining.Remove(pos);

                    int level = clearers.TryGetValue(pos, out var clearer) ? levels[clearer] + 1 : 0;
                    levels[pos] = level;
                }
            }
            Console.Error.WriteLine("Levels computed");

            var depths = new Dictionary<Coords, int>();
            foreach (var level in levels.OrderByDescending(level => level.Value))
                depths[level.Key] = depths.TryGetValue(level.Key, out var childDepth) ? childDepth + 1 : 0;
            Console.Error.WriteLine("Depths computed");

            g = new Grid(Grid);
            var bombs = depths.Select(x => new {pos = x.Key, depth = x.Value}).OrderByDescending(x => x.depth).ToList();
            while (bombs.Count > 0)
            {
                g.Tick();
                var bomb = bombs.FirstOrDefault(x => g.GetCell(x.pos.Col, x.pos.Row) == CellContents.Empty);
                if (bomb == null)
                {
                    yield return null;
                    continue;
                }

                yield return new Bomb(bomb.pos.Col, bomb.pos.Row);
                g.PutBomb(bomb.pos.Col, bomb.pos.Row);
                bombs.RemoveAll(x => x.pos.Equals(bomb.pos));
            }
        }


    }

    public class Player
    {
        public Player(TextReader input, TextWriter output, TextWriter debug, IStrategy strategy)
        {
            Input = input;
            Output = output;
            Debug = debug;
            Strategy = strategy;
        }

        private TextReader Input { get; }

        private TextWriter Output { get; }

        private TextWriter Debug { get; }

        private Grid Grid { get; set; }

        private IStrategy Strategy { get; }

        private Queue<Bomb> Plan { get; set; }

        public void Init()
        {
            var inputs = Input.ReadLine().Split(' ');
            int width = int.Parse(inputs[0]); // width of the firewall grid
            int height = int.Parse(inputs[1]); // height of the firewall grid
            Grid = new Grid(width, height);
            for (int row = 0; row < height; row++)
            {
                string rowContents = Input.ReadLine(); // one line of the firewall grid
                Debug.WriteLine(rowContents);
                for (int col = 0; col < width; col++)
                {
                    switch (rowContents[col])
                    {
                        case '@':
                            Grid.PutNode(col, row);
                            break;
                        case '#':
                            Grid.PutPassive(col, row);
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        public void Act()
        {
            var input = Input.ReadLine();
            Debug.WriteLine(input);
            var inputs = input.Split(' ');
            int rounds = int.Parse(inputs[0]); // number of rounds left before the end of the game
            int bombs = int.Parse(inputs[1]); // number of bombs left

            if (Plan == null)
            {
                Plan = new Queue<Bomb>(Strategy.PlanActions(Grid, rounds, bombs));
            }

            var bomb = Plan.Dequeue();
            if (bomb != null)
            {
                Grid.PutBomb(bomb.Col, bomb.Row);
                Output.WriteLine($"{bomb.Col} {bomb.Row}");
            }
            else
            {
                Output.WriteLine("WAIT");
            }

            Grid.Tick();
            Grid.Dump(Debug);
        }
    }

    public struct Coords
    {
        public Coords(int col, int row)
        {
            Row = row;
            Col = col;
        }

        public int Row;
        public int Col;

        public override string ToString()
        {
            return $"{Col}:{Row}";
        }
    }

    public class Grid
    {
        public Grid(int width, int height)
        {
            Width = width;
            Height = height;
            Cells = new CellContents[width, height];
        }

        public Grid(Grid other)
        {
            Width = other.Width;
            Height = other.Height;
            Cells = (CellContents[,]) other.Cells.Clone();
            Bombs = new List<Bomb>(other.Bombs.Select(b => new Bomb(b)));
        }

        public int Width { get; }

        public int Height { get; }

        private CellContents[,] Cells { get; set; }

        private List<Bomb> Bombs { get; } = new List<Bomb>();

        private static Tuple<int, int>[] Directions = new[]
        {
            new Tuple<int, int>(0, 1),
            new Tuple<int, int>(0, -1),
            new Tuple<int, int>(1, 0),
            new Tuple<int, int>(-1, 0),
        };

        private static string CellTranslation = ".#@B";

        public int GetBombCount() => Bombs.Count;

        public CellContents GetCell(int col, int row)
        {
            return Cells[col, row];
        }

        public void PutNode(int col, int row)
        {
            Cells[col, row] = CellContents.Node;
        }

        public void PutPassive(int col, int row)
        {
            Cells[col, row] = CellContents.Passive;
        }

        public void PutBomb(int col, int row)
        {
            Cells[col, row] = CellContents.Bomb;
            Bombs.Add(new Bomb(col, row));
        }

        public void Clear(int col, int row)
        {
            Cells[col, row] = CellContents.Empty;
        }

        public delegate void NodeCleared(int col, int row);

        public void Tick(NodeCleared action = null)
        {
            var triggered = new Queue<Bomb>();
            foreach (var bomb in Bombs)
            {
                bomb.Timer--;
                if (bomb.Timer == 0)
                    triggered.Enqueue(bomb);
            }

            while (triggered.Count > 0)
            {
                var bomb = triggered.Dequeue();
                Explode(bomb, triggered.Enqueue, action);
                Bombs.Remove(bomb);
                Clear(bomb.Col, bomb.Row);
            }
        }

        public void Dump(TextWriter output)
        {
            for (int row = 0; row < Height; row++)
            {
                var rowDump = new char[Width];
                for (int col = 0; col < Width; col++)
                    rowDump[col] = CellTranslation[(int) Cells[col, row]];
                output.WriteLine(rowDump);
            }
        }

        public delegate void CellVisit(int col, int row, CellContents contents);

        public int Count(CellContents type)
        {
            return Cells.Cast<CellContents>().Count(c => c == type);
        }

        public void VisitBlast(int col, int row, CellVisit action)
        {
            foreach (var dir in Directions)
            {
                for (int i = 1; i < 4; i++)
                {
                    int blastCol = col + dir.Item1 * i;
                    int blastRow = row + dir.Item2 * i;
                    if (blastRow < 0 || blastRow >= Height || blastCol < 0 || blastCol >= Width)
                        break;

                    var contents = Cells[blastCol, blastRow];
                    if (contents == CellContents.Passive)
                        break;

                    action(blastCol, blastRow, contents);
                }
            }
        }

        public IEnumerable<Coords> EnumerateBlast(int col, int row)
        {
            foreach (var dir in Directions)
            {
                for (int i = 1; i < 4; i++)
                {
                    int blastCol = col + dir.Item1 * i;
                    int blastRow = row + dir.Item2 * i;
                    if (blastRow < 0 || blastRow >= Height || blastCol < 0 || blastCol >= Width)
                        break;

                    var contents = Cells[blastCol, blastRow];
                    if (contents == CellContents.Passive)
                        break;

                    yield return new Coords(blastCol, blastRow);
                }
            }
        }

        private void Explode(Bomb bomb, Action<Bomb> trigger, NodeCleared action)
        {
            VisitBlast(bomb.Col, bomb.Row, (col, row, contents) => {
                switch (contents)
                {
                    case CellContents.Bomb:
                        trigger(Bombs.Find(b => b.Col == col && b.Row == row));
                        break;
                    case CellContents.Node:
                        Clear(col, row);
                        if (action != null)
                            action(col, row);
                        break;
                }
            });
        }
    }

    public class Bomb
    {
        public Bomb(int col, int row)
        {
            Col = col;
            Row = row;
            Timer = 4;
        }

        public Bomb(Bomb other)
        {
            Col = other.Col;
            Row = other.Row;
            Timer = other.Timer;
        }

        public int Col { get; }
        public int Row { get; }
        public int Timer { get; set; }
    }

    public enum CellContents { Empty, Passive, Node, Bomb }
}

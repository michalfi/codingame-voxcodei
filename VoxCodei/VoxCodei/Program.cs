using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace VoxCodei
{
    public class Program
    {
        static void Main(string[] args)
        {
            var player = new Player(Console.In, Console.Out, Console.Error, new GreedyStrategy());

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
            var planGrid = grid.Clone();

            for (int bombNumber = 0; bombNumber < bombCount; bombNumber++)
            {
                var positions = Enumerable.Range(0, grid.Width)
                    .SelectMany(col => Enumerable.Range(0, grid.Height).Select(row => new {col, row}))
                    .Where(pos => planGrid.GetCell(pos.col, pos.row) == CellContents.Empty);

                var effect = positions.Select(pos => new
                {
                    pos.col,
                    pos.row,
                    nodes = CountBlastedNodes(pos.col, pos.row, planGrid)
                });

                var best = effect.OrderByDescending(x => x.nodes).First();
                if (best.nodes == 0)
                {
                    yield return null;
                    continue;
                }

                yield return new Bomb(best.col, best.row);
                planGrid.VisitBlast(best.col, best.row, (col, row, contents) => planGrid.Clear(col, row));
            }

            for (int waitNumber = 0; waitNumber < rounds - bombCount; waitNumber++)
                yield return null;
        }

        private int CountBlastedNodes(int col, int row, Grid grid)
        {
            var count = 0;
            grid.VisitBlast(col, row, (c, r, contents) =>
            {
                if (contents == CellContents.Node) count++;
            });
            return count;
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
    }

    public class Grid
    {
        public Grid(int width, int height)
        {
            Width = width;
            Height = height;
            Cells = new CellContents[width, height];
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

        public void Tick()
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
                Explode(bomb, triggered.Enqueue);
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

        public Grid Clone()
        {
            var clone = new Grid(Width, Height);
            for (int col = 0; col < Width; col++)
            for (int row = 0; row < Height; row++)
                clone.Cells[col, row] = Cells[col, row];
            return clone;
        }

        private void Explode(Bomb bomb, Action<Bomb> trigger)
        {
            VisitBlast(bomb.Col, bomb.Row, (col, row, contents) => {
                switch (contents)
                {
                    case CellContents.Bomb:
                        trigger(Bombs.Find(b => b.Col == col && b.Row == row));
                        break;
                    case CellContents.Node:
                        Clear(col, row);
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

        public int Col { get; }
        public int Row { get; }
        public int Timer { get; set; }
    }

    public enum CellContents { Empty, Passive, Node, Bomb }
}

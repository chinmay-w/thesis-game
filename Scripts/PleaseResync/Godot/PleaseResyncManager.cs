using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;

namespace PleaseResync
{
    public partial class PleaseResyncManager : Node
    {
        [Export] public bool SyncTest;
        private Label SimulationInfo;
        private Label RollbackInfo;
        private Label PingInfo;

        private bool Started;
        private bool Replay;
        
        [Export] protected ushort FrameDelay = 2;
        [Export] protected ushort SimulatedFrameDelay = 0;
        [Export] protected ushort SpectatorDelay = 30;
        [Export] protected uint MaxPlayers = 2;
        [Export] protected uint MaxSpectators = 8;
        [Export] protected uint DeviceCount = 2;
        [Export] protected uint InputSize = 1;
        private uint DEVICE_ID;
        private uint PingId;

        private string[] Adresses = {"127.0.0.1", "127.0.0.1", "127.0.0.1", "127.0.0.1"};
        private ushort[] Ports = {7001, 7002, 7003, 7004};
        private uint[] rollbackFrames = new uint[16];

        public IGameState sessionState;

        LiteNetLibSessionAdapter adapter;
        Session session;
        byte[] LastInput;
        List<SessionAction> sessionActions;
        string InputDebug;
        string SimulationText;
        string PingText;
        string RollbackText;
        List<ReplayInputs> RecordedInputs = new List<ReplayInputs>();

        private Thread GameThread;

        private bool canRender;
        private System.Threading.Mutex mutex = new System.Threading.Mutex();

        private string ShowPingInfo(uint id)
        {
            return session.AllDevices[id].GetRTT().ToString();
        }

        public override void _Ready()
        {
            SimulationInfo = GetTree().Root.GetNode<Label>("Root/CanvasLayer/PleaseResync_UI/Connection_Status/Simulation_Info");
            RollbackInfo = GetTree().Root.GetNode<Label>("Root/CanvasLayer/PleaseResync_UI/Connection_Status/Rollback_Info");
            PingInfo = GetTree().Root.GetNode<Label>("Root/CanvasLayer/PleaseResync_UI/Connection_Status/Ping_Info");
            RecordedInputs.Add(new ReplayInputs());

            if (SimulationInfo != null) SimulationInfo.Text = "";
            if (RollbackInfo != null) RollbackInfo.Text = "";
            if (PingInfo != null) PingInfo.Text = "";
        }

        public override void _PhysicsProcess(double delta)
        {
            if (!Started) return;

            //if (canRender) sessionState.Render();
            mutex.WaitOne();
            sessionState.Render();
            mutex.ReleaseMutex();

            if (SimulationInfo != null) SimulationInfo.Text = NotificationText();
            if (RollbackInfo != null) RollbackInfo.Text = RollbackText;
            if (PingInfo != null) PingInfo.Text = PingText;
        }

        void OnDestroy()
        {
            CloseGame();
        }

        public void CreateConnections(string[] IPAdresses, ushort[] ports)
        {
            for (uint i = 0; i < IPAdresses.Length; i++)
            {
                if (IPAdresses[i] != "") Adresses[i] = IPAdresses[i];
                if (ports[i] > 0) Ports[i] = ports[i];
            }
        }

        protected void StartOnlineGame(IGameState state, uint playerCount, uint ID)
        {
            DEVICE_ID = ID;

            sessionState = state;

            sessionState.Setup();
            
            adapter = new LiteNetLibSessionAdapter(Adresses[DEVICE_ID], Ports[DEVICE_ID]);

            session = new Peer2PeerSession(InputSize, DeviceCount, MaxPlayers, false, adapter);

            LastInput = new byte[InputSize];

            session.SetLocalDevice(DEVICE_ID, 1, FrameDelay);
            
            for (uint i = 0; i < DeviceCount; i++)
            {
                if (i != DEVICE_ID)
                {
                    session.AddRemoteDevice(i, 1, LiteNetLibSessionAdapter.CreateRemoteConfig(Adresses[i], Ports[i]));
                    PingId = i;
                    
                    GD.Print($"Device {i} created");
                }
            }
            
            Replay = false;
            Started = true;

            StartGameThread();
        }

        protected void StartSpectatorMode(IGameState state, uint playerCount, uint ID)
        {
            DEVICE_ID = ID;

            sessionState = state;

            sessionState.Setup();
            
            adapter = new LiteNetLibSessionAdapter(Adresses[DEVICE_ID], Ports[DEVICE_ID]);

            session = new Peer2PeerSession(InputSize, DeviceCount, MaxPlayers, false, adapter);

            LastInput = new byte[InputSize];

            session.AddSpectatorDevice(DEVICE_ID, SpectatorDelay);
            
            for (uint i = 0; i < DeviceCount; i++)
            {
                if (i != DEVICE_ID)
                {
                    session.AddRemoteDevice(i, 1, LiteNetLibSessionAdapter.CreateRemoteConfig(Adresses[i], Ports[i]));
                    PingId = i;
                    
                    GD.Print($"Device {i} created");
                }
            }
            
            Replay = false;
            Started = true;

            StartGameThread();
        }

        protected void StartOfflineGame(IGameState state, uint playerCount)
        {
            sessionState = state;
            sessionState.Setup();

            session = new Peer2PeerSession(InputSize, 1, MaxPlayers, true, null);
            LastInput = new byte[(int)(MaxPlayers * InputSize)];
            session.SetLocalDevice(0, MaxPlayers, SimulatedFrameDelay);

            if (RollbackInfo != null) RollbackInfo.Text = "";
            if (PingInfo != null) PingInfo.Text = "";

            Replay = false;
            Started = true;

            StartGameThread();
        }

        protected void StartReplay(IGameState state, uint playerCount)
        {
            sessionState = state;
            sessionState.Setup();

            session = new Peer2PeerSession(InputSize, 1, MaxPlayers, true, null);
            LastInput = new byte[(int)(MaxPlayers * InputSize)];
            session.SetLocalDevice(0, MaxPlayers, SimulatedFrameDelay);

            if (RollbackInfo != null) RollbackInfo.Text = "";
            if (PingInfo != null) PingInfo.Text = "";

            Replay = true;
            Started = true;

            StartGameThread();
        }

        private void GameLoop()
        {
            while (Started)
            {
                mutex.WaitOne();
                //canRender = false;
                if (!session.IsOffline()) session.Poll();

                if (session.IsRunning())
                {
                    if (Replay)
                        LastInput = ReadInputs(session.Frame());
                    else
                        for (int i = 0; i < LastInput.Length / InputSize; i++)
                        {
                            byte[] inputs = SyncTest ? GetRandomInput() : sessionState.GetLocalInput(i, (int)InputSize);
                            Array.Copy(inputs, 0, LastInput, i * InputSize, inputs.Length);
                        }

                    sessionActions = session.AdvanceFrame(LastInput);
                    
                    foreach (var action in sessionActions)
                    {
                        switch (action)
                        {
                            case SessionAdvanceFrameAction AFAction:
                                InputDebug = InputConstructor(AFAction.Inputs);
                                sessionState.GameLoop(AFAction.Inputs);
                                if (!Replay) RecordInput(AFAction.Frame, AFAction.Inputs);
                                break;
                            case SessionLoadGameAction LGAction:
                                MemoryStream readerStream = new MemoryStream(LGAction.Load());
                                BinaryReader reader = new BinaryReader(readerStream);
                                sessionState.LoadState(reader);
                                break;
                            case SessionSaveGameAction SGAction:
                                MemoryStream writerStream = new MemoryStream();
                                BinaryWriter writer = new BinaryWriter(writerStream);
                                sessionState.SaveState(writer);
                                byte[] state = writerStream.ToArray();
                                SGAction.Save(state, Platform.GetChecksum(state));
                                break;
                        }
                    }

                    string FrameCounter = session.IsOffline() ? $"{session.Frame()}" : $"{session.Frame()} ({session.RemoteFrame()} | {session.FrameAdvantage()} | {session.RemoteFrameAdvantage()})";

                    SimulationText = FrameCounter + $" || ( {InputDebug} )";
                    if (!session.IsOffline())
                    {
                        rollbackFrames[session.Frame() % rollbackFrames.Length] = session.RollbackFrames();
                        RollbackText = "RBF: " + GetAverageRollbackFrames();
                        PingText = "Ping: " + ShowPingInfo(PingId) + " ms";
                    }

                    if (Replay && session.Frame() >= RecordedInputs.Count)
                        CloseGame();
                }
                //canRender = true;
                mutex.ReleaseMutex();
                Thread.Sleep((int)((1 / 60f) * 1000));
            }
        }

        private uint GetAverageRollbackFrames()
        {
            float sumFrames = 0f;
            
            for(int i = 0; i < rollbackFrames.Length; i++)
            {
                sumFrames += rollbackFrames[i];
            }

            sumFrames /= rollbackFrames.Length;

            return (uint)Mathf.RoundToInt(sumFrames);
        }

        private void StartGameThread()
        {
            GameThread = new Thread(() => GameLoop());
            //GameThread.IsBackground = true;
            GameThread.Start();
        }

        private string NotificationText()
        {
            switch (session.State())
            {
                default:
                    return "";
                case 0:
                    return "Syncing...";
                case 1:
                    return SimulationText;
                case 2:
                    return "Connection lost.";
                case 3:
                    return "Fatal desync detected.";
            }
        }

        public void CloseGame()
        {
            Started = false;
            Replay = false;
            sessionState = null;
            if (adapter != null) adapter.Close();
            SimulationInfo.Text = "Disconnected";
        }

        private string InputConstructor(byte[] PlayerInputs)
        {
            string finalString = " ";

            for (int i = 0; i < PlayerInputs.Length; i++)
            {
                finalString += PlayerInputs[i].ToString() + " ";
                if (i < PlayerInputs.Length - 1) finalString += ":: ";
            }

            return finalString;
        }

        private void RecordInput(int frame, byte[] inputs)
        {
            if (frame >= RecordedInputs.Count)
                RecordedInputs.Add(new ReplayInputs());
            
            RecordedInputs[frame] = new ReplayInputs(inputs);
        }

        private byte[] ReadInputs(int frame)
        {
            return RecordedInputs[frame].inputs;
        }

        private byte[] GetRandomInput()
        {
            byte[] cnv = new byte[InputSize];
            for (int i = 0; i < cnv.Length; i++)
            {
                cnv[i] = (byte)Platform.GetRandomUnsignedShort();
            }

            return cnv;
        }

        public virtual void OnlineGame(uint maxPlayers, uint ID){}
        public virtual void Spectate(uint maxPlayers, uint ID){}
        public virtual void LocalGame(uint maxPlayers){}
        public virtual void ReplayMode(uint maxPlayers) {}
    }

    public struct ReplayInputs
    {
        public byte[] inputs;

        public ReplayInputs()
        {
            inputs = new byte[0];
        }

        public ReplayInputs(byte[] i)
        {
            inputs = i;
        }
    };
}

using MiniHospital.Config;
using MiniHospital.Simulation.Characters;

namespace MiniHospital.Simulation.Rooms
{
    public class Room : ServicePoint
    {
        public RoomConfig Config { get; }

        private volatile bool isOpen;
        public bool IsOpen { get => isOpen; set => isOpen = value; }
        
        //Lock mutex
        private readonly Mutex patientMutex = new();
        
        public Room(RoomConfig config): base(config.Staff, config.Patient)
        {
            Config = config;
            IsOpen = false;
        }
        
        public bool TryEnter(Patient patient)
        {
            if (!patientMutex.WaitOne(0))
                return false;

            if (!TryAssignPatient(patient))
            {
                patientMutex.ReleaseMutex();
                return false;
            }
            
            return true;
        }

        public void Exit(Patient patient)
        {
            if (TryReleasePatient(patient))
            {
                patientMutex.ReleaseMutex();
            }
        }
        
        public bool TryReleaseMutex()
        {
            if (!patientMutex.WaitOne(0))
            {
                return false;
            }
            
            patientMutex.ReleaseMutex();
            return true;
        }
    }
}

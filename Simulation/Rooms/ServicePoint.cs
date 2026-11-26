using MiniHospital.Config;
using MiniHospital.Simulation.Characters;
using MiniHospital.Utils;

namespace MiniHospital.Simulation.Rooms
{
    public abstract class ServicePoint
    {
        public Slot StaffSlot { get; }
        public Slot PatientSlot { get; }

        public Employee? Staff { get; set; }
        
        //Mes variables
        private object occupantLock = new();
        private Patient? occupant;
        public Patient? Occupant
        {
            get
            {
                lock (occupantLock)
                {
                    return occupant;
                }
            }
        }

        protected ServicePoint(Slot staffSlot, Slot patientSlot)
        {
            StaffSlot = staffSlot;
            PatientSlot = patientSlot;
        }

        public bool TryAssignPatient(Patient patient)
        {
            lock (occupantLock)
            {
                Chaos.ExecuteIfEnabled();
                if (occupant != null)
                {
                    return false;
                }
                    
                Chaos.ExecuteIfEnabled();
                occupant = patient;
                return true;
            }
        }
        
        public bool TryReleasePatient(Patient patient)
        {
            lock (occupantLock)
            {
                if (occupant != patient)
                {
                    return false;
                }
                    
                occupant = null;
                Chaos.ExecuteIfEnabled();
                return true;
            }
        }
        
        public void AssignPatient(Patient patient)
        {
            if (!TryAssignPatient(patient))
            {
                throw new InvalidOperationException("Poste déjà occupé");
            }
        }

        public void ReleasePatient(Patient? patient)
        {
            lock (occupantLock)
            {
                if (occupant != patient)
                {
                    throw new InvalidOperationException("Libération d'un poste par le mauvais patient");
                }

                occupant = null;
                Chaos.ExecuteIfEnabled();
            }
        }
    }
}

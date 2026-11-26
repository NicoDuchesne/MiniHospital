using MiniHospital.Config;
using MiniHospital.Simulation.Rooms;
using MiniHospital.Utils;

namespace MiniHospital.Simulation.Characters
{
    public class Employee : Character
    {
        public bool IsAvailable => step == Step.WaitForPatient;

        private readonly Hospital hospital;
        private Patient? patient;
        private float patientHandlingEndTime;
        private TimeRange patientHandlingRequiredTime;
        private ServicePoint workPlace;

        private enum Step
        {
            Arriving,   //Pas utilisé dans la verison TP
            WaitForPatient,
            HandlePatient,
            Leaving,
            WaitingForBusDeparting,
            Finished
        }
        
        //MES VARIABLES
        private Thread employeeThread;
        private object employeeLock = new();
        private volatile Step step;

        public Employee(int spriteIndex, ServicePoint workplace, Hospital hospital, TimeRange patientHandlingRequiredTime)
            : base(spriteIndex)
        {
            this.workPlace = workplace;
            SetRenderScale(workplace is Reception ? CharacterConfig.RenderScale : CharacterConfig.DoctorScale);
			this.hospital = hospital;
            PlaceAt(workPlace.StaffSlot);
            step = Step.WaitForPatient;
            this.patientHandlingRequiredTime = patientHandlingRequiredTime;
        }

        public void StartBrain() //lance le thread
        {
            employeeThread = new Thread(launchBrain);
            hospital.AddThread(employeeThread);
            employeeThread.Start();
        }
        
        private void launchBrain()
        {
            while (step != Step.Finished)
            {
                Brain();
                Thread.Sleep(1);
            }
        }

        public void LeaveHospital()
        {
            Chaos.ExecuteIfEnabled();
            if (patient != null)
            {
                //problem
                throw new InvalidOperationException("Un employé reçoit l'ordre de partir alors qu'il n'a pas encore fini avec le patient");
            }
            workPlace.Staff = null;

            MoveTo(NavNode.BusStop);
            step = Step.Leaving;
        }
        public void OnBusDeparted()
        {
            if (step != Step.WaitingForBusDeparting)
                throw new InvalidOperationException("Employé absent du bus");
            step = Step.Finished;
        }

        public void HandlePatient(Patient newPatient)
        {
            lock (employeeLock)
            {
                if(patient != null)
                {
                    //problem
                    throw new InvalidOperationException("Un employé reçoit un patient alors qu'il n'a pas encore fini avec le précédent");
                }
                if(step != Step.WaitForPatient)
                {
                    throw new InvalidOperationException("Un employé reçoit un patient alors qu'il n'est pas en train d'en attendre un");
                }
                patient = newPatient;

                patientHandlingEndTime = hospital.ElapsedTimeSinceStart + patientHandlingRequiredTime.Sample();
                SetBubbletype(BubbleType.Health);
                step = Step.HandlePatient;
            }
        }
        
        public bool TryHandlePatient(Patient newPatient)
        {
            lock (employeeLock)
            {
                if (hospital.Phase != Hospital.DayPhase.Running || patient != null || step != Step.WaitForPatient)
                {
                    return false;
                }
                
                if (patient != null || step != Step.WaitForPatient)
                {
                    return false;
                }
                
                patient = newPatient;
                patientHandlingEndTime = hospital.ElapsedTimeSinceStart + patientHandlingRequiredTime.Sample();
                SetBubbletype(BubbleType.Health);
                step = Step.HandlePatient;
                return true;
            }
        }

        private void FinishHandlingPatient()
        {
            Patient p;
            Room? assigned = null;
            
            if (workPlace is Reception)
            {
                assigned = hospital.ChooseValidRoom(false);
            }
            else if(workPlace is Room room)
            {
                if (room.Config.Type == RoomType.Generalist && hospital.Phase == Hospital.DayPhase.Running && Random.Shared.NextDouble() < 0.35)
                {
                    assigned = hospital.ChooseValidRoom(true);
                }
            }

            lock (employeeLock)
            {
                p = patient ?? throw new Exception("FinishHandlingPatient appelé alors que patient est null");
                patient = null;
                SetBubbletype(BubbleType.None);
                step = Step.WaitForPatient;
            }

            if (workPlace is Reception)
            {
                p.RegistrationFinished(assigned);
            }
            else
            {
                p.ConsultationFinished(assigned);
            }
            
        }

        protected override void Brain()
        {
            switch(step)
            {
                case Step.WaitForPatient:
                    lock (employeeLock)
                    {
                        if (hospital.Phase != Hospital.DayPhase.Running && patient == null && workPlace.Occupant == null)
                        {
                            LeaveHospital();
                        }
                    }
                    break;
                case Step.HandlePatient:
                    if (patient != null)
                    {
                        if (hospital.ElapsedTimeSinceStart >= patientHandlingEndTime)
                        {
                            FinishHandlingPatient();
                        }
                    }
                    else
                    {
                        throw new Exception("Emplyé bloqué en état HandlePatient sans patient à traiter");
                    }
                    break;
                case Step.Leaving:
                    if (!IsMoving)
                    {
                        step = Step.WaitingForBusDeparting;
                        hospital.BoardBusAndWait(this); //monte dans le bus et barrier
                        step = Step.Finished;
                    }
                    break;
                case Step.WaitingForBusDeparting:
                    break;
            }
        }
    }
}

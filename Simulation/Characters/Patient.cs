using MiniHospital.Config;
using MiniHospital.Rendering;
using MiniHospital.Simulation.Rooms;
using MiniHospital.Utils;

namespace MiniHospital.Simulation.Characters
{
    public class Patient : Character
    {
        public Room? AssignedRoom { get; private set; }
        public Reception? AssignedReception { get; private set; }


        private Hospital hospital;
        private volatile Step step = Step.Arriving;

        private GaugeSprite gaugeSprite;
        private bool wasTreated;
        private float waitingStartedAt;
        private Slot? waitingSeat;
        private float maxPatience;
        
        //MES VARIABLES
        private Thread patientThread;
        private volatile bool consultationDone;
        private Room? nextRoom;
        private volatile bool registrationDone;
        private Room? registrationRoom;

        public Patient(int spriteIndex, Hospital hospital, float maxPatience) : base(spriteIndex)
        {
            this.hospital = hospital;
            SetRenderScale(CharacterConfig.PatientScale);
            gaugeSprite = new GaugeSprite();
            this.maxPatience = maxPatience;
        }
        
        //lance le thread
        public void StartBrain()
        {
            patientThread = new Thread(launchBrain);
            hospital.AddThread(patientThread);
            patientThread.Start();
        }

        private void launchBrain()
        {
            while (step != Step.Finished)
            {
                Brain();
                Thread.Sleep(1);
            }
        }

        private enum Step
        {
            Arriving,
            GoingToReception,
            AtReception,
            GoingToWaiting,
            WaitingForReception,
            WaitingForRoom,
            GoingToRoom,
            InConsultation,
            Leaving,
            Finished
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            gaugeSprite.Position = position + CharacterConfig.PatienceGaugePosition;
        }

        public override void GetRenderSprites(List<Sprite> list)
        {
            base.GetRenderSprites(list);
            if (step == Step.WaitingForReception || step == Step.WaitingForRoom)
            	list.Add(gaugeSprite.Copy());
        }

        private void SetCurrentStep(Step next)
        {
            Chaos.ExecuteIfEnabled();
            step = next;

            if(Hospital.CurrentQuestion != 0)
            {
                //cas spéciaux pour les exercices
                if (Hospital.CurrentQuestion == 1 && next == Step.AtReception ||
                    Hospital.CurrentQuestion == 2 && next == Step.WaitingForReception ||
                    Hospital.CurrentQuestion == 3 && next == Step.WaitingForRoom)
                {
                    StartLeaving(false);
                }

            }
        }

        public void RegistrationFinished(Room? room)
        {
            registrationRoom = room;
            registrationDone = true;
        }

        public void ConsultationFinished(Room? specialist)
        {
            nextRoom = specialist;
            consultationDone = true;
        }

        private bool TryGotoReception()
        {
            Reception? reception = hospital.TryGetReception(this);
            if (reception == null)
            {
                return false;
            }
            
            AssignedReception = reception;
            ReleaseWaitingSeat();
            MoveTo(reception.PatientSlot);
            SetBubbletype(BubbleType.Question);
            SetCurrentStep(Step.GoingToReception);
            return true;
        }

        private void GoToWaiting()
        {
            waitingSeat = hospital.TakeFreeSeat();
            MoveTo(waitingSeat);
            SetBubbletype(BubbleType.Wait);
            SetCurrentStep(Step.GoingToWaiting);
        }

        private void ReleaseWaitingSeat()
        {
            if (waitingSeat == null)
                return;
            hospital.ReleaseSeat(waitingSeat);
            waitingSeat = null;
        }

        private bool WaitOrGiveUp()
        {
            float remaining = 1f -
                (hospital.ElapsedTimeSinceStart - waitingStartedAt) / maxPatience;
            gaugeSprite.Value = Math.Clamp(remaining, 0f, 1f);
            SetBubbletype(remaining < 0.25f ? BubbleType.Angry : BubbleType.Wait);

            if (remaining > 0f)
                return false;

            StartLeaving(false);
            return true;
        }

        private void StartLeaving(bool treated)
        {
            wasTreated = treated;
            ReleaseWaitingSeat();

            if (AssignedReception != null)
            {
                hospital.ReleaseReception(AssignedReception, this);
            }
            AssignedReception = null;

            if (AssignedRoom != null)
            {
                AssignedRoom.Exit(this);
            }
            AssignedRoom = null;

            SetBubbletype(treated ? BubbleType.Done : BubbleType.Angry);
            MoveTo(NavNode.Outside);
            SetCurrentStep(Step.Leaving);
        }

        protected override void Brain()
        {
            if (hospital.Phase != Hospital.DayPhase.Running &&
                step != Step.InConsultation && step != Step.AtReception &&
                step != Step.Leaving && step != Step.Finished)
            {
                StartLeaving(false);
                return;
            }
            
            switch (step)
            {
                case Step.Arriving:
                    //cas spéciaux pour les exercices
                    if (Hospital.CurrentQuestion == 1)
                    {
                        MoveTo(HospitalLayout.ReceptionPatients[0]);
                        SetCurrentStep(Step.GoingToReception);
                    }
                    else if (Hospital.CurrentQuestion == 2)
                    {
                        GoToWaiting();
                    }
                    else if (!TryGotoReception())
                    {
                        GoToWaiting();
                    }
                    break;

                case Step.GoingToReception:
                    if (!IsMoving)
                    {
                        SetCurrentStep(Step.AtReception);
                        if (step == Step.AtReception)
                        {
                            Employee? staff = AssignedReception?.Staff;
                            if (staff == null || !staff.TryHandlePatient(this))
                            {
                                StartLeaving(false);
                            }
                        }
                    }
                    break;

                case Step.AtReception:
                    if (registrationDone)
                    {
                        registrationDone = false;
                        AssignedRoom = registrationRoom;
                        hospital.ReleaseReception(AssignedReception!, this);
                        AssignedReception = null;

                        if (AssignedRoom == null || hospital.Phase != Hospital.DayPhase.Running)
                        {
                            StartLeaving(false);
                        }
                        else
                        {
                            GoToWaiting();
                        }
                    }
                    break;

                case Step.GoingToWaiting:
                    if (!IsMoving)
                    {
                        waitingStartedAt = hospital.ElapsedTimeSinceStart;
                        gaugeSprite.Value = 1f;
                        SetCurrentStep(AssignedRoom == null ? Step.WaitingForReception : Step.WaitingForRoom);
                    }
                    break;

                case Step.WaitingForReception:
                    if (!WaitOrGiveUp())
                        TryGotoReception();
                    break;

                case Step.WaitingForRoom:
                    if (WaitOrGiveUp())
                        break;

                    if (hospital.Phase == Hospital.DayPhase.Running &&
                        AssignedRoom != null && AssignedRoom.IsOpen &&
                        AssignedRoom.Staff?.IsAvailable == true &&
                        AssignedRoom.TryEnter(this))
                    {
                        ReleaseWaitingSeat();
                        MoveTo(AssignedRoom);
                        SetBubbletype(BubbleType.Health);
                        SetCurrentStep(Step.GoingToRoom);
                    }
                    break;

                case Step.GoingToRoom:
                    if (!IsMoving)
                    {
                        SetCurrentStep(Step.InConsultation);
                        Employee? employee = AssignedRoom!.Staff;
                        if (employee == null || !employee.TryHandlePatient(this))
                        {
                            StartLeaving(false);
                        }
                    }
                    break;

                case Step.InConsultation:
                    if (consultationDone)
                    {
                        consultationDone = false;
                        Room? specialist = nextRoom;
                        AssignedRoom!.Exit(this);                  
                        AssignedRoom = null;

                        if (specialist != null && hospital.Phase == Hospital.DayPhase.Running)
                        {
                            AssignedRoom = specialist;
                            GoToWaiting();                      
                        }
                        else
                            StartLeaving(specialist == null);
                    }
                    break;

                case Step.Leaving:
                    if (!IsMoving)
                    {
                        SetCurrentStep(Step.Finished);
                        hospital.RegisterPatientDeparture(this, wasTreated);
                    }
                    break;
            }
        }
    }
}

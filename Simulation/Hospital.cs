using MiniHospital.Config;
using MiniHospital.Rendering;
using MiniHospital.Simulation.Characters;
using MiniHospital.Simulation.Rooms;
using MiniHospital.Utils;
using System.Collections.Concurrent;

namespace MiniHospital.Simulation
{
    public class Hospital
    {
        //configuration du TP: valeur de 0 = mode classique, autrmement le numéro de la question permet de mettre les comportements dans le mode adapté à la question
        public static int CurrentQuestion { get; internal set; } = 5;

        //public pour l'affichage uniquement
        public List<Room> Rooms { get; private set; } = new List<Room>();

        private volatile DayPhase phase;
        public DayPhase Phase => phase;  

        public HospitalStats DayStats { get; private set; } = new HospitalStats();
        public HospitalStats TotalStats { get; } = new HospitalStats();

        public int DayNumber { get; private set; }
        public int EmployeeCount { get; private set; }

        public float SpeedMultiplier { get; set; } = 1f;
        
        //MES VARIABLES
        private object charactersLock = new(); //verrou utlisié à chaque fois qu'on veut utiliser la liste characters
        private object busLock = new(); //verrou pour le bus
        private ConcurrentQueue<Slot> freeSeats = new(); //nouvelle liste des freeSeats utilisant une collection concurrent
        private SemaphoreSlim receptionSemaphore; //semaphore pour les receptions
        private int previousReceptionCount; //pour garder le compte du nb de réceptions
        private Barrier busBarrier = new(1); //la barrière pour que le bus attende tout le monde
        private object threadsLock = new(); //lock pour la liste des threads
        private List<Thread> todayThreads = new(); //liste de nos threads
        
        //MES FONCTIONS
        public void AddThread(Thread thread) //ajoute un thread à la liste
        {
            lock (threadsLock)
            {
                todayThreads.Add(thread);
            }
        }
        
        private void JoinTodayThreads() //attend tous les threads et renvoie une exception si un continue de tourner
        {
            Thread[] threads;
            lock (threadsLock)
            {
                threads = todayThreads.ToArray();
                todayThreads.Clear();
            }
            
            foreach (Thread thread in threads)
            {
                if (!thread.Join(TimeSpan.FromSeconds(6)))
                {
                    throw new Exception("Au moins un thread continue de tourner");
                }
            }
        }
        
        public void BoardBusAndWait(Employee employee) //employee monte à bord du bus et s'enregistre à la barrière
        {
            RegisterEmployeeBoarding(employee);
            busBarrier.SignalAndWait();    
        }
        
        //FONCTIONS DEJA PRESENTES (PARFOIS MODIFIEES)
        
        public TimeSpan ClockTime
        {
            get
            {
                if (dayConfig == null)
                    return TimeSpan.FromHours(8);

                float progress = Math.Clamp(
                    elapsedTimeForToday / Math.Max(1f, dayConfig.DayDuration),
                    0f, 1f
                );

                double minutes =
                    dayConfig.OpeningHour * 60.0
                    + progress * (dayConfig.ClosingHour - dayConfig.OpeningHour) * 60.0;

                return TimeSpan.FromMinutes(minutes);
            }
        }

        public float ElapsedTimeSinceStart { get; private set; }
        public static bool CallBrainOnUpdate { get; set; }

        private List<Sprite> FurnituresSprites = new List<Sprite>();

        private List<Character> characters = new List<Character>();
        

        private List<Reception> receptions = new List<Reception>();
        private List<Employee> employeesInBus = new List<Employee>();
        
        //pour l'affichage
        public int EmployeesInBusCount { get { lock (busLock) return employeesInBus.Count; } }


        public enum DayPhase
        {
            Running,
            Closing,
            BusLeaving,
            Finished
        }

        public Hospital()
        {
            Initialize();

            StartDay(new DayConfig());

        }
        
        

        public void Initialize()
        {
            Rooms = new List<Room>();
            foreach (RoomConfig config in HospitalLayout.Rooms)
            {
                Rooms.Add(new Room(config));
            }

            receptions = new List<Reception>();
            for (int i = 0; i < HospitalLayout.ReceptionStaff.Length; i++)
            {
                receptions.Add(new Reception(
                    HospitalLayout.ReceptionStaff[i],
                    HospitalLayout.ReceptionPatients[i]
                ));
            }

            freeSeats.Clear();
            foreach (Slot s in HospitalLayout.WaitingSeats)
            {
                freeSeats.Enqueue(s);
            }
                

            InitializeFurniture();
            Navigation.InitFullGraph();

            lock (charactersLock)
            {
                characters.Clear();
            }
            ElapsedTimeSinceStart = 0;
            CallBrainOnUpdate = Hospital.CurrentQuestion == 0? true : false;
        }

        public void Update(float deltaTime)
        {
            deltaTime *= SpeedMultiplier;
            ElapsedTimeSinceStart += deltaTime;
            HandleDay(deltaTime);

            // copie pour autoriser la suppression du personnage courant dans Brain() sans causer d'exception
            lock (charactersLock)
            {
                foreach (var character in characters.ToArray())
                {
                    character.Update(deltaTime);
                }
            }
        }

        private void HandleDay(float deltaTime)
        {
            switch (Phase)
            {
                case DayPhase.Running:
                    //lock de l'horloge pour les questions 1 à 4
                    if (CurrentQuestion == 0 || Hospital.CurrentQuestion >= 5)
                        elapsedTimeForToday += deltaTime;

                    if (elapsedTimeForToday >= dayConfig!.DayDuration)
                    {
                        elapsedTimeForToday = dayConfig.DayDuration;
                        phase = DayPhase.Closing;
                    }
                    else
                    {

                        if (dayConfig != null)
                        {
                            if (ElapsedTimeSinceStart >= nextArrivalTime)
                            {
                                SpawnPatient();
                                nextArrivalTime = ElapsedTimeSinceStart + dayConfig.PatientInterval.Sample();
                            }
                        }

                    }
                    break;

                case DayPhase.Closing:
                    if (employeesInBus.Count == EmployeeCount && DayStats.Active == 0)
                    {
                        phase = DayPhase.BusLeaving;
                        transitionTimer = 0f;
                    }
                    break;

                case DayPhase.BusLeaving:
                    transitionTimer += deltaTime;
                    if (transitionTimer >= dayConfig!.DayTransitionDelay)
                    {
                        //le bus est censé partir car tous les employé ont signal and wait normalement
                        if (!busBarrier.SignalAndWait(TimeSpan.FromSeconds(6)))
                        {
                            throw new Exception("Il manque des emplyés dans le bus");
                        }
                        lock (busLock)
                        {
                            employeesInBus.Clear();
                        }
                        phase = DayPhase.Finished;
                    }
                    break;

                case DayPhase.Finished:
                    DayConfig newday = new DayConfig();
                    newday.RandomizeOpenServices();
                    StartDay(newday);
                    break;
			}
        }

        public List<Sprite> GetRenderSprites()
        {
            var list = new List<Sprite>(FurnituresSprites);

            lock (charactersLock)
            {
                foreach (var character in characters)
                {
                    character.GetRenderSprites(list);
                }
            }
            
            
            return list;
        }

        private void InitializeFurniture()
        {
            FurnituresSprites = new List<Sprite>();
            foreach (SpriteConfig config in HospitalLayout.Furniture)
            {
                Sprite sprite = new Sprite(
                    TextureManager.Get(config.Texture),
                    config.Source,
                    config.Position,
                    config.Anchor
                );

                sprite.Layer = config.Layer;
                sprite.Scale = config.Scale;

                FurnituresSprites.Add(sprite);
            }
        }

        private DayConfig? dayConfig;
        private float nextArrivalTime;
        private float elapsedTimeForToday;
        private float transitionTimer;

        public Room? ChooseValidRoom(bool wantsSpecialist)
        {
            Room? assigned = null;
            Room[] specialists = Rooms.Where(r =>
                r.IsOpen && r.Staff != null && (r.Config.Type != RoomType.Generalist) == wantsSpecialist
            ).ToArray();

            if (specialists.Length > 0)
                assigned = specialists[Random.Shared.Next(specialists.Length)];
            return assigned;
        }

        public void StartDay(DayConfig config)
        {
            //Avant un nouveau jour, on s'assure que chacuns des éléments de multi threading soient "réinitialisé"
            if (DayNumber > 0)
            {
                //tous les threads soient finis
                JoinTodayThreads(); 
                busBarrier.Dispose();

                //Que la sémaphore a été entièrement libérée
                if (receptionSemaphore.CurrentCount != previousReceptionCount)
                {
                    throw new Exception("La semaphore n'a pas été totalement release");
                }
                    
                //Que tous les mutex soient libres
                foreach (Room room in Rooms)
                {
                    if (!room.TryReleaseMutex())
                    {
                        throw new Exception("Un mutex est toujours lock");
                    }
                }
                    
            }
            
            var toStart = new List<Employee>();//liste des employés
            
            lock (charactersLock)
            {
                if (characters.Count != 0)
                    throw new Exception("Des personnages sont encore presents");
            }
            
            if (DayNumber > 0 && freeSeats.Count != HospitalLayout.WaitingSeats.Length)
                throw new Exception("Certains sièges n'ont pas été libérés la veille");
            if(employeesInBus.Count > 0)
                throw new Exception("Le bus n'est pas vide en début de journée");
            if (DayNumber > 0 && DayStats.Arrived != DayStats.Treated + DayStats.Untreated)
                throw new Exception("Statistiques de la veille incohérentes");

            dayConfig = config;
            //on crée notre sémaphore
            receptionSemaphore = new SemaphoreSlim(config.ReceptionistCount, config.ReceptionistCount);
            previousReceptionCount =  config.ReceptionistCount;

            DayNumber++;
            DayStats = new HospitalStats();

            elapsedTimeForToday = 0f;
            transitionTimer = 0f;
            nextArrivalTime = ElapsedTimeSinceStart + config.PatientInterval.Sample();
            EmployeeCount = 0;


            freeSeats.Clear();
            foreach (Slot s in HospitalLayout.WaitingSeats)
            {
                freeSeats.Enqueue(s);
            }
                

            // Déterminer les salles ouvertes.
            int generalistIndex = 0;
            foreach (Room room in Rooms)
            {
                if(room.Staff != null || room.Occupant != null)
                {
                    throw new Exception("Certaines salles n'ont pas été libérées la veille");
                }
                room.Staff = null;
                room.ReleasePatient(null);

                switch (room.Config.Type)
                {
                    case RoomType.Generalist:
                        room.IsOpen = generalistIndex < config.GeneralistCount;
                        generalistIndex++;
                        break;

                    case RoomType.Radiology:
                        room.IsOpen = config.RadiologyOpen;
                        break;

                    case RoomType.Psychiatry:
                        room.IsOpen = config.PsychiatryOpen;
                        break;
                }
            }

            // Créer les réceptionistes.
            for (int i = 0; i < config.ReceptionistCount; i++)
            {

                Reception reception = receptions[i];
                if (reception.Staff != null || reception.Occupant != null)
                {
                    throw new Exception("Certains accueils n'ont pas été libérées la veille");
                }
                reception.Staff = null;
                reception.ReleasePatient(null);

                Employee employee = new(
                    CharacterConfig.ReceptionitsSpriteIds[Random.Shared.Next(CharacterConfig.ReceptionitsSpriteIds.Length)],
                    reception,
                    this,
                    dayConfig.ReceptionDuration
                );

                reception.Staff = employee;
                employee.PlaceAt(reception.StaffSlot);
                lock (charactersLock)
                {
                    characters.Add(employee);
                    toStart.Add(employee);
                    ++EmployeeCount;
                }
            }

            // Créer un médecin par salle ouverte.
            foreach (Room room in Rooms)
            {
                if (!room.IsOpen)
                    continue;
                TimeRange duration = dayConfig.GeneralistDuration;
                switch(room.Config.Type)
                {
                    case RoomType.Generalist:
                        duration = dayConfig.GeneralistDuration;
                        break;
                    case RoomType.Psychiatry:
                        duration = dayConfig.SpecialistDuration;
                        break;
                    case RoomType.Radiology:
                        duration = dayConfig.SpecialistDuration;
                        break;
                }

                Employee employee = new(
                    CharacterConfig.DoctorSpriteIds[Random.Shared.Next(CharacterConfig.DoctorSpriteIds.Length)],
                    room,
                    this,
                    duration
                );

                room.Staff = employee;
                lock (charactersLock)
                {
                    characters.Add(employee);
                    toStart.Add(employee);
                    ++EmployeeCount;
                }
            }
            
            busBarrier = new Barrier(EmployeeCount + 1); //la barrière du bus est préparée
            phase = DayPhase.Running;
            foreach (var employee in toStart) //on lance les threads de chaque employé
            {
                employee.StartBrain();
            }
        }

        private void SpawnPatient()
        {
            DayStats.Add();
            TotalStats.Add();

            Patient patient = new(
                CharacterConfig.PatientSpriteIds[Random.Shared.Next(CharacterConfig.PatientSpriteIds.Length)],
                this,
                dayConfig!.PatientPatience.Sample());

            patient.PlaceAt(NavNode.Outside);

            lock (charactersLock)
            {
                characters.Add(patient);
                patient.StartBrain();//A chaque spawn patient on lance le thread
            }
        }

        public Slot TakeFreeSeat() //prend une place libre via une Concurrent Queue
        {
            if (freeSeats.TryDequeue(out Slot? seat))
            {
                Chaos.ExecuteIfEnabled();
                return seat;
            }

            return HospitalLayout.StandingWaitingSlots[Random.Shared.Next(HospitalLayout.StandingWaitingSlots.Length)];
        }

        public void ReleaseSeat(Slot seat)
        {
            if (seat.Pose != CharacterAction.Sitting)
            {
                return;
            }
            if (freeSeats.Contains(seat))
            {
                throw new Exception("Un siège vient d'être libéré une 2e fois");
            }

            Chaos.ExecuteIfEnabled();
            freeSeats.Enqueue(seat);
        }
        
        public void RegisterEmployeeBoarding(Employee employee)
        {
            lock (charactersLock)
            {
                if (!characters.Remove(employee))
                {
                    throw new Exception("Enregistrement de départ d'un employé non présent dans l'hopital");
                }
            }

            lock (busLock)
            {
                if (employeesInBus.Contains(employee))
                {
                    throw new Exception("Employé est déjà dans le bus");
                }
                employeesInBus.Add(employee);
            }
        }

        public void RegisterPatientDeparture(Patient patient, bool treated)
        {
            lock (charactersLock)
            {
                if (!characters.Remove(patient))
                {
                    throw new Exception("Enregistrement de départ d'un patient non présent dans l'hopital");
                }
            }
            
            DayStats.Remove(treated);
            TotalStats.Remove(treated);
        }

        public Reception? TryGetReception(Patient patient)
        {
            if (Phase != DayPhase.Running) return null;
            
            if (!receptionSemaphore.Wait(0))
            {
                return null; //si on n'entre pas dans la semaphore on return null, comme ça la patience continue
            }     
            
            foreach (Reception candidate in receptions)
            {
                if (candidate.IsOpen && candidate.Staff != null && candidate.Staff.IsAvailable && candidate.TryAssignPatient(patient))
                {
                    return candidate;
                }
            }
            
            receptionSemaphore.Release();
            return null;
        }
        
        public void ReleaseReception(Reception reception, Patient patient)
        {
            if (reception.TryReleasePatient(patient))
            {
                receptionSemaphore.Release();
            }
            
        }
    }
}
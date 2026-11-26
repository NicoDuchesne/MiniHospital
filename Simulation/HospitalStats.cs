namespace MiniHospital.Simulation
{
    public class HospitalStats
    {
        //the lock
        private object statsLock = new();
        
        //fields
        private int arrived;
        private int treated;
        private int untreated;
        private int active;
        private int score;
        private int combo;
        private int bestCombo;
        
        //properties
        public int Arrived { get { lock (statsLock) return arrived; } }
        public int Treated { get { lock (statsLock) return treated; } }
        public int Untreated { get { lock (statsLock) return untreated; } }
        public int Active { get { lock (statsLock) return active; } }
        public int Score { get { lock (statsLock) return score; } }
        public int Combo { get { lock (statsLock) return combo; } }
        public int BestCombo { get { lock (statsLock) return bestCombo; } }

        //ajout d'un patient
        public void Add()
        {
            lock (statsLock)
            {
                arrived++; 
                active++;
            }
        }
        
        //depart d'un patient
        public void Remove(bool wasTreated)
        {
            lock (statsLock)
            {
                active--;
                if (wasTreated)
                {
                    treated++;
                    combo++;
                    score += 100 * combo;
                    if (combo > bestCombo)
                        bestCombo = combo;
                }
                else
                {
                    untreated++;
                    combo = 0;
                }
            }
        }
        
        //ancienne fontion update
        public void UpdateScore(bool wasTreated)
        {
            lock (statsLock)
            {
                if (wasTreated)
                {
                    combo++;
                    score += 100 * combo;
                    if (combo > bestCombo)
                        bestCombo = combo;
                }
                else
                {
                    combo = 0;
                }
            }
        }
    }
}

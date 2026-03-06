using UnityEngine;
using UnityEngine.SceneManagement;

public class MASceneReset : MonoBehaviour
{
    // --- Singleton Pattern ---
    public static MASceneReset Instance { get; private set; }

    private void Awake()
    {
        // If an instance already exists and it's not this one, destroy this object
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        // Optional: Keep this object alive across scene loads if you want 
        // a persistent manager. Otherwise, let it reload with the scene.
        // DontDestroyOnLoad(gameObject);
    }

    // --- Core Logic ---

    /// <summary>
    /// Reloads the current active scene and ensures physics/time scales are normalized.
    /// </summary>
    public void ResetCurrentScene()
    {
        // 1. Get the index of the currently active scene
        int currentSceneIndex = SceneManager.GetActiveScene().buildIndex;

        // 2. Reload the scene
        // Note: This will destroy all non-persistent objects and reset the simulation
        SceneManager.LoadScene(currentSceneIndex);

        // 3. Reset Engine States
        // Important for ML/RL loops to ensure no leftover "slow-mo" or paused states
        Time.timeScale = 1f;

        // Reset the virtual clock if using MATimeStepManager
        if (MATimeStepManager.Instance != null)
        {
            MATimeStepManager.Instance.fixedTime = 0f;
        }

        Debug.Log("<color=orange>MASceneReset: Scene Reset Triggered.</color>");
    }
}
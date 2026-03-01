using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneReset : MonoBehaviour
{
    public void ResetCurrentScene()
    {
        // Get the index of the currently active scene
        int currentSceneIndex = SceneManager.GetActiveScene().buildIndex;

        // Reload it
        SceneManager.LoadScene(currentSceneIndex);

        // Optional: Ensure time scale is reset if you ever use Pause menus
        Time.timeScale = 1f;

        Debug.Log("Scene Reset Triggered");
    }
}
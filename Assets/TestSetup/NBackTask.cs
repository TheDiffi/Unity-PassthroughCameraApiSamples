using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class NBackTask : MonoBehaviour
{

    [SerializeField]
    private LightColorSetter lightColorSetter; // Added LightColorSetter reference

    private float stimulusDuration = 2f;
    private float interStimulusInterval = 2f;
    private float feedbackDuration = 0.1f;

    private int nBackLevel = 1;
    private int totalTrials = 30;

    [Header("Communication")]
    [SerializeField]
    private MasterConnector currentConnector;

    private Color[] colors = { Color.red, Color.green, Color.blue, Color.yellow, Color.magenta, Color.white };
    private int currentTrial = 0;
    private int sessionNumber = -1;
    private string studyId = "NOCONF";
    private int[] colorSequence;
    private bool awaitingResponse;
    private bool targetTrial;
    private float trialStartTime;
    private List<NbackTrialData> trialDataList = new List<NbackTrialData>();
    private Dictionary<int, long> trialOnsetTimes = new Dictionary<int, long>(); // Store onset times by trial number

    private bool isPaused = false;
    private Coroutine trialCoroutine;
    private Coroutine debugCoroutine;
    private bool inDebugMode = false;
    private bool eventsSetup = false;

    void Start()
    {
        // Check if the connector is set and connected
        if (currentConnector == null)
        {
            Debug.LogError("No connector set. Please assign a NodeJS or ADB connector.");
            throw new InvalidOperationException("No connector set. Please assign a NodeJS or ADB connector.");
        }

        if (!eventsSetup)
        {
            // Register handlers using the NBack task-specific registration method
            currentConnector.RegisterNBackHandler("start", _ => StartTask());
            currentConnector.RegisterNBackHandler("pause", _ => PauseTask());
            currentConnector.RegisterNBackHandler("resume", _ => ResumeTask());
            currentConnector.RegisterNBackHandler("stop", _ => ExitTask());
            currentConnector.RegisterNBackHandler("debug", _ => DebugMode());
            currentConnector.RegisterNBackHandler("exit-debug", _ => ExitDebug());
            currentConnector.RegisterNBackHandler("exit", _ => ExitTask());
            currentConnector.RegisterNBackHandler("get-data", _ => GetData());
            currentConnector.RegisterNBackHandler("configure", (data) => ConfigureTask(data));
            eventsSetup = true;
        }

    }

    void StartTask()
    {
        Debug.Log("Starting NBack task");
        if (trialCoroutine != null)
            StopCoroutine(trialCoroutine);

        if (colorSequence == null || colorSequence.Length == 0)
        {
            string errorMessage = "No color sequence configured. Configure the task before starting.";
            Debug.LogError(errorMessage);
            currentConnector.SendNBackEvent("configure-error", errorMessage);

            // Don't start the trials
            return;
        }

        trialDataList.Clear();
        currentTrial = 0;
        SessionStopwatch.StartSession();
        trialCoroutine = StartCoroutine(RunTrials());
    }

    void PauseTask()
    {
        Debug.Log("Pausing NBack task");
        isPaused = !isPaused;
        Debug.Log($"Task paused: {isPaused}");
        currentConnector.SendNBackEvent("task-paused", isPaused ? "Task paused" : "Task resumed");
    }

    void ResumeTask()
    {
        Debug.Log("Resuming NBack task");
        isPaused = false;
        currentConnector.SendNBackEvent("task-resumed", "Task resumed");
    }

    void DebugMode()
    {
        Debug.Log("Debug mode activated");
        inDebugMode = true;

        // Start color cycling coroutine
        if (debugCoroutine != null)
            StopCoroutine(debugCoroutine);

        debugCoroutine = StartCoroutine(CycleColorsInDebugMode());

        // Log start of debug mode
        currentConnector.SendNBackEvent("debug-mode", "Debug mode activated");
    }

    void ExitDebug()
    {
        Debug.Log("Exiting debug mode");
        inDebugMode = false;

        // Stop color cycling
        if (debugCoroutine != null)
        {
            StopCoroutine(debugCoroutine);
            debugCoroutine = null;
        }

        lightColorSetter.TurnOff();
        currentConnector.SendNBackEvent("debug-mode", "Debug mode deactivated");
    }

    IEnumerator CycleColorsInDebugMode()
    {
        int colorIndex = 0;

        while (inDebugMode)
        {
            // Cycle through available colors
            lightColorSetter.SetColor(colors[colorIndex]);

            // Log the current color in debug mode
            Debug.Log($"Debug mode color: {GetColorNameFromIndex(colorIndex)}");

            // Move to next color
            colorIndex = (colorIndex + 1) % colors.Length;

            // Wait for 1 second before changing color
            yield return new WaitForSeconds(1f);
        }
    }

    // Helper function to get color name from index - for logging purposes
    private string GetColorNameFromIndex(int colorIndex)
    {
        return colorIndex switch
        {
            0 => "Red",
            1 => "Green",
            2 => "Blue",
            3 => "Yellow",
            4 => "Purple",
            5 => "White",
            _ => "Unknown"
        };
    }



    void ConfigureTask(object data)
    {
        Debug.Log("Configuring NBack task with data: " + (data != null ? data.ToString() : "null"));
        try
        {
            Dictionary<string, object> paramsDict = null;

            // The data is a JObject, so convert it to a dictionary
            if (data is Newtonsoft.Json.Linq.JObject jObject)
            {
                paramsDict = jObject.ToObject<Dictionary<string, object>>();
            }
            else
            {
                currentConnector.SendNBackEvent("configure-error", "Expected JObject format for configuration");
                Debug.LogError("Failed to parse configuration data: " + (data != null ? data.ToString() : "null"));
                return;
            }

            // Process parameters - we know numeric values are of type long
            if (paramsDict.TryGetValue("studyId", out object studyIdObj) && studyIdObj is string studyIdStr)
            {
                studyId = studyIdStr;
            }

            if (paramsDict.TryGetValue("sessionNumber", out object sessionObj) && sessionObj is long sessionLong)
            {
                sessionNumber = (int)sessionLong;
            }

            // Process numeric parameters - we know they are all of type long
            if (paramsDict.TryGetValue("stimDuration", out object stimDurationObj) && stimDurationObj is long stimDurationLong)
            {
                stimulusDuration = stimDurationLong / 1000f;
            }

            if (paramsDict.TryGetValue("interStimulusInterval", out object isiObj) && isiObj is long isiLong)
            {
                interStimulusInterval = isiLong / 1000f;
            }

            if (paramsDict.TryGetValue("nBackLevel", out object nBackObj) && nBackObj is long nBackLong)
            {
                nBackLevel = (int)nBackLong;
            }

            if (paramsDict.TryGetValue("trialsNumber", out object trialsObj) && trialsObj is long trialsLong)
            {
                totalTrials = (int)trialsLong;
            }

            // Parse the sequence data - working with JArray directly
            if (paramsDict.TryGetValue("sequence", out object sequenceObj) && sequenceObj is Newtonsoft.Json.Linq.JArray jArray)
            {
                List<int> colorIndices = new List<int>();

                foreach (var item in jArray)
                {
                    string colorName = item.ToString();
                    int colorIndex = GetColorIndexFromName(colorName);
                    if (colorIndex >= 0)
                    {
                        colorIndices.Add(colorIndex);
                    }
                    else
                    {
                        Debug.LogWarning($"Unknown color name in sequence: {colorName}");
                    }
                }

                if (colorIndices.Count > 0)
                {
                    colorSequence = colorIndices.ToArray();
                    totalTrials = colorSequence.Length;
                    Debug.Log($"Parsed sequence with {colorSequence.Length} colors");
                }
                else
                {
                    Debug.LogWarning("No valid colors found in sequence");
                }
            }

            currentConnector.SendNBackEvent("configure-success", "Configuration applied successfully");
            Debug.Log($"Configuration applied: stimDuration={stimulusDuration}s, ISI={interStimulusInterval}s, nBackLevel={nBackLevel}, trials={totalTrials}");
        }
        catch (Exception ex)
        {
            currentConnector.SendNBackEvent("configure-error", "Error parsing configuration: " + ex.Message);
            Debug.LogException(ex);
        }
    }

    // Helper function to convert color name to color index
    private int GetColorIndexFromName(string colorName)
    {
        return colorName.ToLower() switch
        {
            "red" => 0,
            "green" => 1,
            "blue" => 2,
            "yellow" => 3,
            "purple" => 4,
            _ => -1,
        };
    }

    IEnumerator RunTrials()
    {
        while (currentTrial < totalTrials)
        {
            if (isPaused)
            {
                yield return null;
                continue;
            }

            targetTrial = currentTrial >= nBackLevel && colorSequence[currentTrial] == colorSequence[currentTrial - nBackLevel];

            // Capture exact stimulus presentation time using the Stopwatch
            long stimulusOnsetTime = SessionStopwatch.get.ElapsedMilliseconds;

            // Show the stimulus color
            lightColorSetter.SetColor(colors[colorSequence[currentTrial]]);
            trialStartTime = Time.time;
            awaitingResponse = true;

            // Store onset time for this trial to use in HandleResponse
            trialOnsetTimes[currentTrial] = stimulusOnsetTime;
            // Wait for input instead of automatically advancing
            while (awaitingResponse)
            {
                yield return null;
                // This loop will exit when HandleResponse is called by button press
            }

            // The stimulus is already black and feedback has been shown in HandleResponse
            // Now wait for the inter-stimulus interval before the next trial
            if (currentTrial < totalTrials - 1)
            {
                lightColorSetter.TurnOff();
                // Wait for ISI, but check for pause during this time
                float waited = 0;
                while (waited < interStimulusInterval)
                {
                    yield return null;
                    waited += Time.deltaTime;
                }
            }

            currentTrial++;
        }

        SessionStopwatch.StopSession();
        currentConnector.SendNBackEvent("task-complete", "Task complete");
    }

    public void OnCorrectButtonPressed()
    {
        HandleResponse(true);
    }

    public void OnWrongButtonPressed()
    {
        HandleResponse(false);
    }

    void HandleResponse(bool isConfirm)
    {
        // Log button presses if in debug mode
        if (inDebugMode)
        {
            string buttonType = isConfirm ? "Correct" : "Wrong";
            Debug.Log($"Debug mode: {buttonType} button pressed");
            currentConnector.SendNBackEvent("debug-button-press", buttonType);
            return;
        }

        if (!awaitingResponse) return;

        // Capture response time using the Stopwatch for precision
        long responseTimeMs = SessionStopwatch.get.ElapsedMilliseconds;

        // Get the stored onset time for the current trial
        long stimulusOnsetTimeMs = trialOnsetTimes[currentTrial];

        // Calculate reaction time in milliseconds using precise Stopwatch values
        int reactionTimeMs = (int)(responseTimeMs - stimulusOnsetTimeMs);

        var result = targetTrial == isConfirm
            ? targetTrial ? "Correct response" : "Correct rejection"
            : targetTrial ? "Missed target" : "False alarm";

        // First, show visual feedback and capture when stimulus actually ends
        _ = StartCoroutine(FeedbackFlash());

        // Capture the actual time when the stimulus is hidden
        long stimulusEndTimeMs = SessionStopwatch.get.ElapsedMilliseconds;

        // Then, send event to nodejs
        currentConnector.SendNBackEvent("trial-complete", result);

        // Record the trial data with the actual end time
        RecordTrial(isConfirm, reactionTimeMs, responseTimeMs, stimulusEndTimeMs, result);

        // Mark that we've received the response
        awaitingResponse = false;
    }

    IEnumerator FeedbackFlash()
    {
        lightColorSetter.SetColor(Color.white);
        yield return new WaitForSeconds(feedbackDuration);
        lightColorSetter.TurnOff();
    }

    void RecordTrial(bool response, int reactionTimeMs, long responseTimeMs, long stimulusEndTimeMs, string result)
    {
        // Convert elapsed times to local date time for reference (optional)
        DateTime stimulusOnsetLocalTime = SessionStopwatch.ElapsedToLocalTime(trialOnsetTimes[currentTrial]);
        DateTime responseLocalTime = SessionStopwatch.ElapsedToLocalTime(responseTimeMs);
        DateTime stimulusEndLocalTime = SessionStopwatch.ElapsedToLocalTime(stimulusEndTimeMs);


        trialDataList.Add(new NbackTrialData
        {
            study_id = studyId,
            session_number = sessionNumber,
            timestamp = stimulusOnsetLocalTime.ToString("o"),  // ISO 8601 format with ms precision
            // Use currentTrial + 1 to match the stimulus number with the trial number (1-based index)
            stimulus_number = currentTrial + 1,
            stimulus_color = GetColorNameFromIndex(colorSequence[currentTrial]),
            is_target = targetTrial,
            response_made = response,
            is_correct = targetTrial == response,
            stimulus_onset_time = stimulusOnsetLocalTime.ToString("o"),
            response_time = responseLocalTime.ToString("o"),
            reaction_time = reactionTimeMs,
            stimulus_end_time = stimulusEndLocalTime.ToString("o")
        });
    }

    void ExitTask()
    {
        Debug.Log("Exiting NBack task");
        if (trialCoroutine != null)
            StopCoroutine(trialCoroutine);
        lightColorSetter.TurnOff();
        trialDataList.Clear();
    }

    void GetData()
    {
        Debug.Log("Sending trial data to Node.js controller");
        foreach (var data in trialDataList)
            currentConnector.SendNBackEvent("trial-data", data.ToString());

        // Change from "nback-data-complete" to match what the Node.js controller expects
        currentConnector.SendNBackEvent("data-complete", "Data transfer complete");
    }

    private class NbackTrialData
    {
        public string study_id;
        public int session_number;
        public string timestamp;
        public int stimulus_number;
        public string stimulus_color;
        public bool is_target;
        public bool response_made;
        public bool is_correct;
        public string stimulus_onset_time;
        public string response_time;
        public int reaction_time;
        public string stimulus_end_time;

        public override string ToString()
        {
            return JsonUtility.ToJson(this);
        }
    }


}


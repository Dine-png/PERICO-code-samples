using BugForgee.SteamNetworking;
using UnityEngine;

/// <summary>
/// 프레임 스파이크 감지기.
///
/// "간간이 매우매우 끊긴다" 같은 증상은 재현 조건을 잡기 전까지는 추측만 하게 된다.
/// 이 컴포넌트는 임계값을 넘긴 프레임이 나올 때마다 그 프레임의 정황을 한 줄로 남긴다.
/// 어느 쪽이 원인인지(물리 캐치업 / GC / 네트워크 재전송) 로그만 보고 갈라낼 수 있다.
///
/// 씬에 배치할 필요 없다. 게임 시작 시 스스로 붙는다.
/// 끄려면 <see cref="Enabled"/> 를 false 로 두거나 DISABLE_FRAME_SPIKE_LOGGER 를 정의한다.
/// </summary>
[DisallowMultipleComponent]
public class FrameSpikeLogger : MonoBehaviour
{
    /// <summary>이 시간을 넘긴 프레임을 스파이크로 본다. 60fps 기준 한 프레임은 16.7ms 다.</summary>
    [SerializeField] private float spikeThresholdMs = 100f;

    [Tooltip("연속으로 튈 때 로그가 도배되지 않도록 하는 최소 간격(초).")]
    [SerializeField] private float logCooldown = 0.5f;

    [Tooltip("이 주기마다 구간 요약을 남긴다(초). 0이면 요약하지 않는다.")]
    [SerializeField] private float summaryInterval = 30f;

    [Tooltip("씬 로드 직후 이만큼은 무시한다(초). 로딩 자체의 멈춤이 로그를 덮는 것을 막는다.")]
    [SerializeField] private float sceneWarmupGrace = 3f;

    /// <summary>런타임에 끄고 켤 수 있다. 자동 설치 여부도 이 값을 따른다.</summary>
    public static bool Enabled = true;

    private int fixedStepsThisFrame;
    private int lastGcCount;
    private int lastResentCommands;
    private float nextLogTime;

    // 구간 요약용
    private float summaryStartTime;
    private float worstFrameMs;
    private int spikeCount;
    private int frameCount;

#if !DISABLE_FRAME_SPIKE_LOGGER
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (!Enabled)
            return;

        GameObject host = new GameObject(nameof(FrameSpikeLogger));
        DontDestroyOnLoad(host);
        host.AddComponent<FrameSpikeLogger>();
    }
#endif

    private float graceUntil;

    private void Awake()
    {
        lastGcCount = System.GC.CollectionCount(0);
        summaryStartTime = Time.realtimeSinceStartup;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        graceUntil = Time.realtimeSinceStartup + sceneWarmupGrace;
    }

    private void OnDestroy()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // 씬 로딩 직후의 멈춤은 에셋 로드/셰이더 컴파일이지 게임플레이 스파이크가 아니다.
    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        graceUntil = Time.realtimeSinceStartup + sceneWarmupGrace;
    }

    private void FixedUpdate()
    {
        // 한 프레임에 물리가 몇 번 돌았는지가 캐치업 폭주를 가려내는 핵심 지표다.
        fixedStepsThisFrame++;
    }

    private void Update()
    {
        int steps = fixedStepsThisFrame;
        fixedStepsThisFrame = 0;

        if (!Enabled)
            return;

        float frameMs = Time.unscaledDeltaTime * 1000f;
        bool inGrace = Time.realtimeSinceStartup < graceUntil;

        if (!inGrace)
        {
            frameCount++;
            if (frameMs > worstFrameMs)
                worstFrameMs = frameMs;
        }

        int gcCount = System.GC.CollectionCount(0);
        int gcDelta = gcCount - lastGcCount;
        lastGcCount = gcCount;

        int resent = GetResentReliableCommands();
        int resentDelta = Mathf.Max(0, resent - lastResentCommands);
        lastResentCommands = resent;

        if (frameMs < spikeThresholdMs || inGrace)
        {
            LogSummaryIfDue();
            return;
        }

        spikeCount++;

        if (Time.unscaledTime < nextLogTime)
            return;

        nextLogTime = Time.unscaledTime + logCooldown;

        Debug.LogWarning(
            $"[FrameSpike] {frameMs:F0}ms  물리스텝={steps}  GC={gcDelta}  " +
            $"재전송+{resentDelta}  핑={GetPing()}ms  씬={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");

        LogSummaryIfDue();
    }

    private void LogSummaryIfDue()
    {
        if (summaryInterval <= 0f)
            return;

        float elapsed = Time.realtimeSinceStartup - summaryStartTime;
        if (elapsed < summaryInterval)
            return;

        float avgFps = frameCount / Mathf.Max(0.001f, elapsed);
        Debug.Log($"[FrameSpike] 최근 {elapsed:F0}초 — 평균 {avgFps:F0}fps, 최악 프레임 {worstFrameMs:F0}ms, 스파이크 {spikeCount}회");

        summaryStartTime = Time.realtimeSinceStartup;
        worstFrameMs = 0f;
        spikeCount = 0;
        frameCount = 0;
    }

    private static int GetResentReliableCommands()
    {
        if (!PhotonNetwork.IsConnected || PhotonNetwork.NetworkingClient == null)
            return 0;

        return PhotonNetwork.NetworkingClient.LoadBalancingPeer.ResentReliableCommands;
    }

    private static int GetPing()
    {
        return PhotonNetwork.IsConnected ? PhotonNetwork.GetPing() : 0;
    }
}

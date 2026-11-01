using BugForgee.SteamNetworking;
using UnityEngine;

/// <summary>
/// 스냅샷 버퍼 기반 트랜스폼 동기화.
///
/// PhotonTransformViewClassic 은 "가장 최근에 받은 좌표 하나"를 향해 매 프레임 Lerp 한다.
/// 그래서 패킷 사이(1/SerializationRate)에 목표에 도달해 멈췄다가 다음 패킷이 오면 다시
/// 가속하는 동작이 반복되고, 이것이 원격 플레이어가 "툭툭" 끊겨 보이는 원인이었다.
///
/// 실제 보간은 <see cref="TransformSnapshotBuffer"/> 가 담당한다.
/// 보간 지연은 실측 패킷 간격에 맞춰 자동으로 조절되므로 회선마다 따로 맞출 필요가 없다.
///
/// 주의: 이 컴포넌트가 transform 을 Update 에서 직접 쓰므로,
/// 원격 오브젝트의 Rigidbody 보간(RigidbodyInterpolation)은 반드시 꺼야 한다.
/// 켜져 있으면 렌더 직전에 물리 포즈로 다시 덮어써서 여기서 계산한 값이 버려진다.
/// </summary>
[DisallowMultipleComponent]
public class NetworkTransformSync : MonoBehaviourPun, IPunObservable
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void EnsureNetworkRates()
    {
        PhotonNetwork.SendRate = 40;
        PhotonNetwork.SerializationRate = 25;
    }

    [Header("Interpolation")]
    [Tooltip("보간 지연 하한(초). 회선이 아주 좋아도 이보다 짧게는 안 내려간다.")]
    [SerializeField] private float minDelay = 0.04f;

    [Tooltip("보간 지연 상한(초). 지터가 심해도 이보다 더 뒤처지지는 않는다.")]
    [SerializeField] private float maxDelay = 0.18f;

    [Tooltip("버퍼가 말랐을 때 마지막 속도로 외삽할 최대 시간. 0이면 외삽하지 않는다.")]
    [SerializeField] private float maxExtrapolation = 0.12f;

    [Header("Remote Impulse Playback")]
    [Tooltip("원격 플레이어가 날아갈 때 적용할 짧은 보간 지연. 물리 외삽으로 지형을 뚫는 현상을 막는다.")]
    [SerializeField, Min(0f)] private float impulsePlaybackDelay = 0.08f;

    [Tooltip("원격 넉백/던지기 중 허용할 외삽 시간. 0이면 마지막 스냅샷을 유지한다.")]
    [SerializeField, Min(0f)] private float impulseMaxExtrapolation;

    [Tooltip("일어난 직후 던지기 보간에서 일반 이동 보간으로 전환하는 시간.")]
    [SerializeField, Min(0f)] private float standUpTransitionDuration = 0.15f;

    [Tooltip("보관할 스냅샷 최대 개수.")]
    [SerializeField] private int bufferCapacity = 32;

    private TransformSnapshotBuffer buffer;
    private double minimumAcceptedServerTime = double.NegativeInfinity;
    private bool remoteImpulsePlayback;
    private float standUpTransitionElapsed = -1f;

    private const int RequiredImpulseSnapshots = 2;

    private TransformSnapshotBuffer Buffer
    {
        get
        {
            if (buffer == null)
            {
                buffer = new TransformSnapshotBuffer(bufferCapacity)
                {
                    MinDelay = minDelay,
                    MaxDelay = maxDelay
                };
            }

            return buffer;
        }
    }

    private void OnEnable()
    {
        Buffer.Clear();
        minimumAcceptedServerTime = double.NegativeInfinity;
        remoteImpulsePlayback = false;
        standUpTransitionElapsed = -1f;
    }

    private void Update()
    {
        if (photonView == null || photonView.IsMine || !PhotonNetwork.IsConnectedAndReady)
            return;

        bool hasEnoughSnapshots = !remoteImpulsePlayback || Buffer.Count >= RequiredImpulseSnapshots;
        GetPlaybackSettings(out float playbackDelay, out float allowedExtrapolation);

        if (hasEnoughSnapshots &&
            Buffer.Sample(PhotonNetwork.Time, allowedExtrapolation, Time.deltaTime,
                          out Vector3 position, out Quaternion rotation, playbackDelay))
        {
            transform.localPosition = position;
            transform.localRotation = rotation;
        }

        // 플레이어 하나가 대표로 진단 요약을 남긴다.
        TransformSnapshotBuffer.TickDiagnostics(Buffer.CurrentDelay, Buffer.Count, Buffer.LastTransit);
    }

    private void GetPlaybackSettings(out float playbackDelay, out float allowedExtrapolation)
    {
        float impulseExtrapolation = Mathf.Min(maxExtrapolation, impulseMaxExtrapolation);
        playbackDelay = remoteImpulsePlayback ? impulsePlaybackDelay : -1f;
        allowedExtrapolation = remoteImpulsePlayback ? impulseExtrapolation : maxExtrapolation;

        if (remoteImpulsePlayback || standUpTransitionElapsed < 0f)
            return;

        standUpTransitionElapsed += Time.deltaTime;
        float duration = Mathf.Max(0.0001f, standUpTransitionDuration);
        float transition = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.Clamp01(standUpTransitionElapsed / duration));

        playbackDelay = Mathf.Lerp(impulsePlaybackDelay, Buffer.CurrentDelay, transition);
        allowedExtrapolation = Mathf.Lerp(impulseExtrapolation, maxExtrapolation, transition);

        if (standUpTransitionElapsed >= duration)
            standUpTransitionElapsed = -1f;
    }

    public void ResetRemoteInterpolation()
    {
        if (photonView == null || photonView.IsMine)
            return;

        Buffer.Clear();
        remoteImpulsePlayback = true;
        standUpTransitionElapsed = -1f;
        minimumAcceptedServerTime = PhotonNetwork.Time;
    }

    public void FinishRemoteImpulsePlaybackSmoothly()
    {
        if (photonView == null || photonView.IsMine)
            return;

        remoteImpulsePlayback = false;
        standUpTransitionElapsed = standUpTransitionDuration > 0f ? 0f : -1f;
    }

    public void StopRemoteImpulsePlayback()
    {
        if (photonView == null || photonView.IsMine)
            return;

        Buffer.Clear();
        remoteImpulsePlayback = false;
        standUpTransitionElapsed = -1f;
        minimumAcceptedServerTime = PhotonNetwork.Time;
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(transform.localPosition);
            stream.SendNext(transform.localRotation);
        }
        else
        {
            Vector3 position = (Vector3)stream.ReceiveNext();
            Quaternion rotation = (Quaternion)stream.ReceiveNext();

            if (info.SentServerTime < minimumAcceptedServerTime)
                return;

            Buffer.Push(info.SentServerTime, PhotonNetwork.Time, position, rotation);
        }
    }
}

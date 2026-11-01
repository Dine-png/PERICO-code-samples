using UnityEngine;

/// <summary>
/// 스킬 하나의 실제 효과. 플레이어 오브젝트에 붙이고
/// <see cref="PlayerSkillController"/>가 장착 중인 스킬과 ID가 맞는 것만 발동시킨다.
///
/// 발동은 모든 클라이언트에서 호출된다 — 로컬 플레이어인지는 <c>isLocal</c>로 구분한다.
/// 상태 변경(체력, 이동 등)은 소유 클라이언트에서만, 연출은 양쪽에서 재생할 것.
/// </summary>
// [DisallowMultipleComponent] 를 붙이면 안 된다 — 이 속성은 **상속된다**.
// 붙이면 플레이어에 스킬 효과가 하나만 올라가고, 스킬을 바꿔도 두 번째 효과는
// AddComponent 가 조용히 null 을 돌려준다("효과 미구현" 로그의 원인이었다).
// 스킬마다 서로 다른 파생 타입이므로 여러 개가 공존해야 정상이다.
public abstract class SkillEffectBase : MonoBehaviour
{
    /// <summary>이 컴포넌트가 담당하는 스킬.</summary>
    public abstract SkillId SkillId { get; }

    /// <summary>발동 시점에 채워지는 스킬 설정값. Awake 이후에 대입된다.</summary>
    protected SkillData Data { get; private set; }

    public int UpgradeLevel { get; private set; }
    private SkillCastSnapshot snapshot;
    public float EffectDuration => snapshot != null ? snapshot.duration : SkillUpgradeRules.EffectDuration(Data, UpgradeLevel);
    public float HoldDuration => snapshot != null ? snapshot.holdDuration : SkillUpgradeRules.HoldDuration(Data, UpgradeLevel);

    /// <summary>컨트롤러가 장착 스킬을 확정할 때 호출한다.</summary>
    public void Bind(SkillData data, int upgradeLevel = 0)
    {
        Data = data;
        UpgradeLevel = Mathf.Clamp(upgradeLevel, 0, SkillProgression.MaxLevel);
        snapshot = null;
    }

    protected float CastValue(int index, float value)
        => snapshot != null && snapshot.values != null && index < snapshot.values.Length ? snapshot.values[index] : value;

    protected virtual float[] CaptureValues() => System.Array.Empty<float>();

    public SkillCastSnapshot CaptureSnapshot(int version)
    {
        snapshot = new SkillCastSnapshot
        {
            skillId = (int)SkillId, level = UpgradeLevel, version = version,
            duration = EffectDuration, holdDuration = HoldDuration,
            cooldown = SkillUpgradeRules.Cooldown(Data, UpgradeLevel), values = CaptureValues()
        };
        snapshot.activeDuration = ActiveDuration;
        return snapshot;
    }

    public void BindSnapshot(SkillData data, SkillCastSnapshot values)
    {
        Bind(data, values.level);
        snapshot = values;
    }

    public SkillCastSnapshot Preview(SkillData data, int level)
    {
        SkillData savedData = Data;
        int savedLevel = UpgradeLevel;
        SkillCastSnapshot savedSnapshot = snapshot;
        Bind(data, level);
        SkillCastSnapshot preview = CaptureSnapshot(0);
        preview.activeDuration = ActiveDuration;
        Data = savedData;
        UpgradeLevel = savedLevel;
        snapshot = savedSnapshot;
        return preview;
    }

    /// <summary>
    /// 발동 순간부터 효과가 완전히 끝날 때까지의 시간(초).
    /// 쿨타임은 이 시간이 지난 뒤에야 돌기 시작한다.
    /// 지속 시간 외에 추가 대기(전송 딜레이, 페이드 아웃 등)가 있는 스킬은 재정의한다.
    /// </summary>
    public virtual float ActiveDuration => EffectDuration;

    /// <summary>
    /// 스킬 발동. <paramref name="isLocal"/>이 true면 이 클라이언트가 시전자다.
    /// </summary>
    public abstract void Activate(bool isLocal);

    /// <summary>
    /// 즉발형 스킬이 지금 발동돼도 되는가. false 면 키를 눌러도 쿨타임을 쓰지 않고 무시한다.
    /// 지평좌표 변환기처럼 "조준한 대상이 있어야 하는" 스킬이 재정의한다.
    /// </summary>
    public virtual bool CanActivate => true;

    /// <summary>
    /// Q를 누르고 있는 동안 조준 표시(핑)를 띄우고, Q를 뗄 때 발동하는 스킬인지.
    /// </summary>
    public virtual bool ActivateOnRelease => false;

    /// <summary>Q를 누르고 있는 동안 매 프레임 호출(로컬만).</summary>
    public virtual void OnAimHold() { }

    /// <summary>조준이 취소되거나 끝났을 때 호출(로컬만).</summary>
    public virtual void OnAimCanceled() { }

    /// <summary>
    /// 지금 홀드 진행도를 올려도 되는가. false 면 진행도가 그 자리에 멈춘다(줄지는 않는다).
    /// 해킹처럼 "조준이 잡혀야 차오르는" 스킬이 재정의한다.
    /// </summary>
    public virtual bool CanAdvanceHold => true;

    /// <summary>
    /// 홀드 진행 중 매 프레임 호출(로컬만). 차징 연출이 필요한 스킬만 재정의한다.
    /// </summary>
    /// <param name="progress">0~1 진행도.</param>
    public virtual void OnHoldProgress(float progress) { }

    /// <summary>홀드가 취소됐을 때(로컬만). 차징 연출을 되돌린다.</summary>
    public virtual void OnHoldCanceled() { }
}

[System.Serializable]
public sealed class SkillCastSnapshot
{
    public int skillId;
    public int level;
    public int version;
    public float duration;
    public float holdDuration;
    public float cooldown;
    public float activeDuration;
    public float[] values;
}

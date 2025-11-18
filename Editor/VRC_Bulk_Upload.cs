using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditorInternal;
using VRC.Core;
using VRC.SDK3.Avatars;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using VRC.SDKBase.Editor.BuildPipeline;
using PeanutTools_VRC_Bulk_Upload;
using VRC.SDKBase;

public class VRC_Bulk_Upload : EditorWindow {
    enum State {
        Idle,
        Building,
        Uploading,
        Success,
        Failed
    }

    enum Action {
        None,
        Build,
        Test,
        BuildAndUpload
    }

    struct AvatarState {
        public Action action;
        public State state;
        public string successfulBuildTime;
        public SdkBuildState? buildState;
        public SdkUploadState? uploadState;
        public float uploadProgress;
        public System.Exception exception;
    }

    Vector2 scrollPosition;
    static Dictionary<string, AvatarState> avatarStates = new Dictionary<string, AvatarState>();
    static CancellationTokenSource GetAvatarCancellationToken = new CancellationTokenSource();
    static CancellationTokenSource BuildAndUploadCancellationToken = new CancellationTokenSource();
    static VRCAvatarDescriptor currentVrcAvatarDescriptor;

    [MenuItem("Tools/VRC Bulk Upload")]
    public static void ShowWindow() {
        var window = GetWindow<VRC_Bulk_Upload>();
        window.titleContent = new GUIContent("VRC Bulk Upload");
        window.minSize = new Vector2(400, 200);
    }

    void OnGUI() {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        CustomGUI.LargeLabel("VRC Bulk Upload");
        CustomGUI.ItalicLabel("Bulks and uploads all active VRChat avatars in your scenes.");

        CustomGUI.LineGap();

        if (!APIUser.IsLoggedIn)
        {
            CustomGUI.WarningLabel("Not logged into VRChat");
            CustomGUI.ItalicLabel("Open the VRChat SDK panel to log in.");
            CustomGUI.LineGap();
        }

		if (APIUser.IsLoggedIn && !hasAgreedToCopyrightAgreement)
		{
			CustomGUI.WarningLabel("Must agree before building.");
			if (CustomGUI.PrimaryButton($"VRCSDK Copyright Agreement"))
			{
				AskForCopyrightAgreement();
			}
			CustomGUI.LineGap();
		}

		CustomGUI.LargeLabel("Avatars In Scenes");
        CustomGUI.HorizontalRule();

        RenderAllAvatarsAndInScene();
        CustomGUI.LineGap();
        CustomGUI.HorizontalRule();

        int count = GetUploadableCount();

        EditorGUI.BeginDisabledGroup(!APIUser.IsLoggedIn || !hasAgreedToCopyrightAgreement);
        if (CustomGUI.PrimaryButton($"Build And Upload All ({count})"))
        {
            // if (EditorUtility.DisplayDialog("Confirm", $"Are you sure you want to build and upload {count.ToString()} VRChat avatars?", "Yes", "No")) {
            _ = BuildAndUploadAllAvatars();
            // }
        }
        EditorGUI.EndDisabledGroup();

		EditorGUILayout.EndScrollView();
    }

// HELPERS

    async Task<VRCAvatar> GetVRCAvatarFromDescriptor(VRCAvatarDescriptor vrcAvatarDescriptor) {
        var blueprintId = vrcAvatarDescriptor.GetComponent<PipelineManager>().blueprintId;

        Debug.Log($"VRC_Bulk_Upload :: Fetching avatar for '{vrcAvatarDescriptor.gameObject.name}' ('{blueprintId}')...");

        var avatarData = await VRCApi.GetAvatar(blueprintId, true, cancellationToken: GetAvatarCancellationToken.Token);

        return avatarData;
    }

    string notLoggedIn = "You must be logged in to VRChat to upload avatars. (Open the SDK panel if you havn't recently)";

    async Task BuildAndUploadAllAvatars() {
        var avatars = GetActiveVrchatAvatars();
        Debug.Log($"VRC_Bulk_Upload :: Building and uploading {avatars.Length} VRChat avatars...");

        foreach (var avatar in avatars) {
            if (GetAvatarUploadableStatus(avatar) == 1)
            {
                await BuildAndUploadAvatar(avatar);
            }
        }
    }

    //Unused
/*    async Task BuildAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building '{vrcAvatarDescriptor.gameObject.name}'...");
        
        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.Build);

            string bundlePath = await builder.Build(vrcAvatarDescriptor.gameObject);
            
            Debug.Log($"VRC_Bulk_Upload :: '{vrcAvatarDescriptor.gameObject.name}' built to '{bundlePath}'");

            SetAvatarState(vrcAvatarDescriptor, State.Success);
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
        }
    }*/

    async Task BuildAndUploadAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        if (!APIUser.IsLoggedIn)
        {
            Debug.LogError(notLoggedIn);
            return;
        }
        Debug.Log($"VRC_Bulk_Upload :: Building and uploading '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.BuildAndUpload);

            var vrcAvatar = await GetVRCAvatarFromDescriptor(vrcAvatarDescriptor);

            //Redundant safety check that ensures vrcAvatar.ID == the pipeline manager ID
            //This can permenantly brick a VRC avatar even if reuploaded, so just in case... Seems to be a bug with SDK 3.5.0
            var blueprintId = vrcAvatarDescriptor.GetComponent<PipelineManager>().blueprintId;
            if (vrcAvatar.ID != blueprintId) {
                throw new System.Exception($"Avatar ID mismatch: {vrcAvatar.ID} != {blueprintId}.");
            }

            // TODO: Support thumbnail image upload?
            // TODO: Add/Support a Cancel button?
            await AddCopyrightAgreement(blueprintId);
            await builder.BuildAndUpload(vrcAvatarDescriptor.gameObject, vrcAvatar, cancellationToken: BuildAndUploadCancellationToken.Token);
        
            SetAvatarState(vrcAvatarDescriptor, State.Success);
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
        }
    }


    async Task BuildAndTestAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building and testing '{vrcAvatarDescriptor.gameObject.name}'...");

        if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
            throw new System.Exception("No builder found");
        }

        try {
            await builder.BuildAndTest(vrcAvatarDescriptor.gameObject);
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
        }
    }

    private static bool hasAgreedToCopyrightAgreement = false;
	public static bool AskForCopyrightAgreement()
	{
		hasAgreedToCopyrightAgreement = EditorUtility.DisplayDialog("VRC Bulk Upload: VRCSDK Agreement",
			VRCCopyrightAgreement.AgreementText + "\n\nI authorize this tool to sign this agreement on my behalf for ALL bulked avatars this Unity session",
			"OK", "NO");

        return hasAgreedToCopyrightAgreement;
	}

	//MIT License Copyright(c) 2023 anatawa12
	//https://github.com/anatawa12/ContinuousAvatarUploader
	private static async Task AddCopyrightAgreement(string blueprint)
	{
		const string key = "VRCSdkControlPanel.CopyrightAgreement.ContentList";
		var keyText = SessionState.GetString(key, "");
		var list = string.IsNullOrEmpty(keyText)
			? new List<string>()
			: SessionState.GetString(key, "").Split(';').ToList();
		if (list.Contains(blueprint)) return;
		list.Add(blueprint);
		SessionState.SetString(key, string.Join(";", list));

		await VRCApi.ContentUploadConsent(new VRCAgreement {
			AgreementCode = "content.copyright.owned",
			AgreementFulltext = VRCCopyrightAgreement.AgreementText,
			ContentId = blueprint,
			Version = 1,
		});
	}

	GameObject[] GetRootObjects() {
        int countLoaded = SceneManager.sceneCount;
        Scene[] scenes = new Scene[countLoaded];
 
        for (int i = 0; i < countLoaded; i++)
        {
            scenes[i] = SceneManager.GetSceneAt(i);
        }

        GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

        foreach (var scene in scenes) {
            if (scene.isLoaded && scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene()) {
                rootObjects = rootObjects.Concat(scene.GetRootGameObjects()).ToArray();
            }
        }

        return rootObjects.ToArray();
    }

    VRCAvatarDescriptor[] GetActiveVrchatAvatars() {
        GameObject[] rootObjects = GetRootObjects();
        
        var vrcAvatarDescriptors = new List<VRCAvatarDescriptor>();

        foreach (var rootObject in rootObjects) {
            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();
            bool isActive = rootObject.activeInHierarchy;

            if (isActive && vrcAvatarDescriptor != null) {
                vrcAvatarDescriptors.Add(vrcAvatarDescriptor);
            }
        }

        return vrcAvatarDescriptors.ToArray();
    }

    int GetUploadableCount()
    {
        var avatars = GetActiveVrchatAvatars();
        int count = 0;

        foreach (var avatar in avatars)
        {
            if (GetAvatarUploadableStatus(avatar) == 1)
            {
                count++;
            }
        }

        return count;
    }

    bool GetCanAvatarBeBuilt(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return vrcAvatarDescriptor != null && vrcAvatarDescriptor.gameObject.GetComponent<Animator>() != null;
    }

    int GetAvatarUploadableStatus(VRCAvatarDescriptor vrcAvatarDescriptor) {
        if (!APIUser.IsLoggedIn || !hasAgreedToCopyrightAgreement)
        {
            return 0;
        }
        if (vrcAvatarDescriptor.gameObject.GetComponent<PipelineManager>() == null)
        {
            return -2;
        }
        if (string.IsNullOrEmpty(vrcAvatarDescriptor.gameObject.GetComponent<PipelineManager>().blueprintId))
        {
            return -2;
        }
        if (!GetCanAvatarBeBuilt(vrcAvatarDescriptor))
        {
            return -1;
		}
        return 1;
    }

	static AvatarState GetAvatarRootState(VRCAvatarDescriptor vrcAvatarDescriptor) {
        if (!avatarStates.ContainsKey(vrcAvatarDescriptor.gameObject.name)) {
            Debug.Log("No State exists, creating...");
            avatarStates[vrcAvatarDescriptor.gameObject.name] = new AvatarState() {
                state = State.Idle
            };
        }
        return avatarStates[vrcAvatarDescriptor.gameObject.name];
    }

    static void SetAvatarRootState(VRCAvatarDescriptor vrcAvatarDescriptor, AvatarState newRootState) {
        avatarStates[vrcAvatarDescriptor.gameObject.name] = newRootState;
    }

    static void SetAvatarAction(VRCAvatarDescriptor vrcAvatarDescriptor, Action newAction) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Debug.Log($"VRC_Bulk_Upload :: Action '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.action}' => '{newAction}'");

        existingState.action = newAction;

        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor, State newState, System.Exception exception = null) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Debug.Log($"VRC_Bulk_Upload :: State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.state}' => '{newState}'");

        existingState.state = newState;
        existingState.exception = exception;

        if (newState == State.Success) {
            existingState.successfulBuildTime = DateTime.Now.ToString("h:mm:ss tt");
        }
        else {
            existingState.successfulBuildTime = null;
        }
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarBuildState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkBuildState? newBuildState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Debug.Log($"VRC_Bulk_Upload :: Build State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.buildState}' => '{newBuildState}'");

        existingState.buildState = newBuildState;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarUploadState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkUploadState? newUploadState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);
        
        Debug.Log($"VRC_Bulk_Upload :: Upload State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.uploadState}' => '{newUploadState}'");

        existingState.uploadState = newUploadState;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarUploadProgress(VRCAvatarDescriptor vrcAvatarDescriptor, float newUploadProgress)
    {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);
        
        //Debug.Log($"VRC_Bulk_Upload :: Upload Progress '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.uploadProgress}' => '{newUploadProgress}'");

        existingState.uploadProgress = newUploadProgress;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    //     void SetAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor, State newState, SdkBuildState? newBuildState, SdkUploadState? newUploadState, System.Exception exception = null) {
    //     var existingState = avatarStates[vrcAvatarDescriptor];
        
    //     avatarStates[vrcAvatarDescriptor] = new AvatarState() {
    //         state = ((existingState != null && newState == null) ? existingState.state : newState),
    //         buildState = newBuildState,
    //         uploadState = newUploadState,
    //         exception = exception
    //     };
    // }

// RENDER GUI

    void RenderAllAvatarsAndInScene() {
        GameObject[] rootObjects = GetRootObjects();

        var hasRenderedAtLeastOne = false;

        foreach (var rootObject in rootObjects) {
            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();
            bool isActive = rootObject.activeInHierarchy;

            if (isActive && vrcAvatarDescriptor != null) {
                if (hasRenderedAtLeastOne) {
                    CustomGUI.LineGap();
                } else {
                    hasRenderedAtLeastOne = true;
                }

                CustomGUI.MediumLabel($"{rootObject.name}");

                GUILayout.BeginHorizontal();

                if (CustomGUI.TinyButtonShort("View")) {
                    Utils.FocusGameObject(rootObject);
                }

                //I never used this.
/*                EditorGUI.BeginDisabledGroup(!GetCanAvatarBeBuilt(vrcAvatarDescriptor));
                    if (CustomGUI.TinyButtonShort("Build")) {
                       _ = BuildAvatar(vrcAvatarDescriptor);
                    }
                EditorGUI.EndDisabledGroup();*/

                if (CustomGUI.TinyButtonShort("Test"))
                {
                    _ = BuildAndTestAvatar(vrcAvatarDescriptor);
                }

                var uploadableStatus = GetAvatarUploadableStatus(vrcAvatarDescriptor);

				EditorGUI.BeginDisabledGroup(uploadableStatus != 1);
                    if (CustomGUI.TinyButton("Build & Upload")) {
                        _ = BuildAndUploadAvatar(vrcAvatarDescriptor);
                    }
                EditorGUI.EndDisabledGroup();

				if (uploadableStatus == -2)
				{
					CustomGUI.WarningLabel("No valid Blueprint ID.");
				}

				//Sometimes this errors, dunno why.
				try
                {
                    RenderAvatarState(vrcAvatarDescriptor);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning(e);
                }

                GUILayout.EndHorizontal();
            }
        }
    }

    string GetMessageForException(System.Exception e) {
        if (e is ApiErrorException) {
            return $"{(e as ApiErrorException).StatusCode}: {(e as ApiErrorException).ErrorMessage}";
        } else {
            return e.Message;
        }
    }

    void RenderAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor) {
        AvatarState avatarState = GetAvatarRootState(vrcAvatarDescriptor);

        // Debug.Log($"Rendering '{vrcAvatarDescriptor.gameObject.name}' action '{avatarState.action}' state '{avatarState.state}'");

        switch (avatarState.state) {
            case State.Idle:
                GUI.contentColor = Color.white;
                GUILayout.Label("");
                break;
            case State.Building:
                GUI.contentColor = new Color(0.8f, 0.8f, 1f, 1);
                GUILayout.Label("Building");
                GUI.contentColor = Color.white;
                break;
            case State.Uploading:
                GUI.contentColor = new Color(0.8f, 0.8f, 1f, 1);
                int progressPercent = Mathf.RoundToInt(avatarState.uploadProgress * 100);
                GUILayout.Label($"Uploading {progressPercent}%");
                GUI.contentColor = Color.white;
                break;
            case State.Success:
                GUI.contentColor = new Color(0.8f, 1f, 0.8f, 1);
                GUILayout.Label("Success" + (avatarState.successfulBuildTime != null ? $" ~ {avatarState.successfulBuildTime}" : ""));
                GUI.contentColor = Color.white;
                break;
            case State.Failed:
                GUI.contentColor = new Color(1f, 0.8f, 0.8f, 1);
                GUILayout.Label($"{(avatarState.exception != null ? GetMessageForException(avatarState.exception) : "Failed (see console)") }");
                GUI.contentColor = Color.white;
                break;
            default:
                throw new System.Exception($"Unknown state {avatarState.state}");
        }
    }

// VRCHAT SDK

    class VRCSDK_Extension {
        [InitializeOnLoadMethod]
        public static void RegisterSDKCallback() {
            Debug.Log("VRC_Bulk_Upload :: SDK.RegisterSDKCallback");
            VRCSdkControlPanel.OnSdkPanelEnable += AddBuildHook;
        }

        private static void AddBuildHook(object sender, System.EventArgs e) {
            if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                builder.OnSdkBuildStart += OnBuildStarted;
                builder.OnSdkUploadStart += OnUploadStarted;

                // 
                // event EventHandler<object> OnSdkBuildStart;
                // event EventHandler<string> OnSdkBuildProgress;
                // event EventHandler<string> OnSdkBuildFinish;
                // event EventHandler<string> OnSdkBuildSuccess;
                // event EventHandler<string> OnSdkBuildError;

                // event EventHandler<SdkBuildState> OnSdkBuildStateChange;
                // SdkBuildState BuildState { get; }

                // // Upload Events
                // event EventHandler OnSdkUploadStart;
                //event EventHandler<(string status, float percentage)> OnSdkUploadProgress;
                // event EventHandler<string> OnSdkUploadFinish;
                // event EventHandler<string> OnSdkUploadSuccess;
                // event EventHandler<string> OnSdkUploadError;

                builder.OnSdkBuildStateChange += OnSdkBuildStateChange;
                builder.OnSdkUploadStateChange += OnSdkUploadStateChange;
                builder.OnSdkUploadProgress += OnSdkUploadProgressChange;
            }
        }

        public int callbackOrder => 0;

        private static void OnBuildStarted(object sender, object target) {
            var name = ((GameObject)target).name;
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnBuildStarted :: Building '{name}'...");

            currentVrcAvatarDescriptor = ((GameObject)target).GetComponent<VRCAvatarDescriptor>();

            SetAvatarState(currentVrcAvatarDescriptor, State.Building);
            SetAvatarBuildState(currentVrcAvatarDescriptor, null);
            SetAvatarUploadState(currentVrcAvatarDescriptor, null);
        }

        private static void OnSdkBuildStateChange(object sender, SdkBuildState newState) {
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnSdkBuildStateChange :: Build state for '{currentVrcAvatarDescriptor.gameObject.name}' is now '{newState}'");

            SetAvatarBuildState(currentVrcAvatarDescriptor, newState);
            SetAvatarUploadState(currentVrcAvatarDescriptor, null);

            if (newState == SdkBuildState.Success && GetAvatarRootState(currentVrcAvatarDescriptor).action == Action.Build) {
                SetAvatarState(currentVrcAvatarDescriptor, State.Success);
            }
        }

        private static void OnUploadStarted(object sender, object target) {
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnUploadStarted :: Uploading...");

            SetAvatarState(currentVrcAvatarDescriptor, State.Uploading);
            SetAvatarBuildState(currentVrcAvatarDescriptor, null);
            SetAvatarUploadState(currentVrcAvatarDescriptor, null);
        }

        private static void OnSdkUploadStateChange(object sender, SdkUploadState newState) {
            // NOTE: This gets called for build state change too for some reason

            if (newState == null) {
                return;
            }

            Debug.Log($"VRC_Bulk_Upload :: SDK.OnSdkUploadStateChange :: Upload state for '{currentVrcAvatarDescriptor.gameObject.name}' is now '{newState}'");

            SetAvatarBuildState(currentVrcAvatarDescriptor, null);
            SetAvatarUploadState(currentVrcAvatarDescriptor, newState);

            if (newState == SdkUploadState.Success) {
                SetAvatarState(currentVrcAvatarDescriptor, State.Success);
            }
        }

        private static void OnSdkUploadProgressChange(object sender, (string status, float percentage) progress)
        {
            //this gets mad at me for not being on the main thread... if you know how to fix it lmk.
            try
            {
                SetAvatarUploadProgress(currentVrcAvatarDescriptor, progress.percentage);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(e);
            }
            
        }

        [InitializeOnLoad]
        public class PreuploadHook : IVRCSDKPreprocessAvatarCallback {
            // This has to be before -1024 when VRCSDK deletes our components
            public int callbackOrder => -90000;

            public bool OnPreprocessAvatar(GameObject clonedAvatarGameObject) {
                Debug.Log($"VRC_Bulk_Upload :: SDK.OnPreprocessAvatar :: '{clonedAvatarGameObject.name}'");
                return true;
            }
        }
    }

    public class OnBuildRequest : IVRCSDKBuildRequestedCallback {
        public int callbackOrder => -90001;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType) {
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnBuildRequested :: '{requestedBuildType}'");
            return true;
        }
    }
}
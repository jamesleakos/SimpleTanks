/*  This file is part of the "Tanks Multiplayer" project by FLOBUK.
 *  You are only allowed to use these resources if you've bought them from the Unity Asset Store.
 * 	You shall not license, sublicense, sell, resell, transfer, assign, distribute or
 * 	otherwise make available to any third party the Service or the Content. */

using UnityEngine;
using UnityEngine.UI;
using Mirror;

namespace TanksMP
{          
    /// <summary>
    /// Networked player class implementing movement control and shooting.
	/// Contains both server and client logic in an authoritative approach.
    /// </summary> 
	public class Player : NetworkBehaviour
    {      
        /// <summary>
        /// Player name synced across the network.
        /// </summary>
		[HideInInspector]
		[SyncVar]
        public string myName;
        
        /// <summary>
        /// UI Text displaying the player name.
        /// </summary>
        public Text label;
        
        /// <summary>
        /// Team value assigned by the server.
        /// </summary>
		[HideInInspector]
		[SyncVar]
        public int teamIndex;

        /// <summary>
        /// Current health value.
        /// </summary>
		[SyncVar(hook = "OnHealthChange")]
        public int health = 10;
        
        /// <summary>
        /// Maximum health value at game start.
        /// </summary>
		[HideInInspector]
        public int maxHealth;

        /// <summary>
        /// Current shield value absorbing hits.
        /// </summary>
		[SyncVar(hook = "OnShieldChange")]
        public int shield = 0;

        /// <summary>
        /// Current turret rotation and shooting direction.
        /// </summary>
        [HideInInspector]
		[SyncVar(hook = "OnTurretRotation")]
        public int turretRotation;

        /// <summary>
        /// Amount of special ammunition left.
        /// </summary>
		[HideInInspector]
		[SyncVar]
        public int ammo = 0;
        
        /// <summary>
        /// Index of currently selected bullet.
        /// </summary>
		[HideInInspector]
		[SyncVar]
        public int currentBullet = 0;
        
        /// <summary>
        /// Delay between shots.
        /// </summary>
        public float fireRate = 0.75f;
        
        /// <summary>
        /// Movement speed in all directions.
        /// </summary>
        public float moveSpeed = 8f;

        /// <summary>
        /// UI Slider visualizing health value.
        /// </summary>
        public Slider healthSlider;
        
        /// <summary>
        /// UI Slider visualizing shield value.
        /// </summary>
        public Slider shieldSlider;

        /// <summary>
        /// Clip to play when a shot has been fired.
        /// </summary>
        public AudioClip shotClip;
        
        /// <summary>
        /// Clip to play on player death.
        /// </summary>
        public AudioClip explosionClip;
        
        /// <summary>
        /// Object to spawn on shooting.
        /// </summary>
        public GameObject shotFX;
        
        /// <summary>
        /// Object to spawn on player death.
        /// </summary>
        public GameObject explosionFX;

        /// <summary>
        /// Turret to rotate with look direction.
        /// </summary>
        public Transform turret;
        
        /// <summary>
        /// Position to spawn new bullets at.
        /// </summary>
        public Transform shotPos;
      
        /// <summary>
        /// Array of available bullets for shooting.
        /// </summary>
        public GameObject[] bullets;
        
        /// <summary>
        /// MeshRenderers that should be highlighted in team color.
        /// </summary>
        public MeshRenderer[] renderers;

        /// <summary>
        /// Last player gameobject that killed this one.
        /// </summary>
        [HideInInspector]
        public GameObject killedBy;
        
        /// <summary>
        /// Reference to the camera following component.
        /// </summary>
        [HideInInspector]
        public FollowTarget camFollow;

        //limit for sending turret rotation updates
        private float sendRate = 0.1f;
        
		//timestamp when next rotate update should happen
        private float nextRotate;
		
        //timestamp when next shot should happen
        private float nextFire;
        
        //reference to this rigidbody
        #pragma warning disable 0649
		private Rigidbody rb;
		#pragma warning restore 0649


        //called before SyncVar updates
        void Awake()
        {
			//saving maximum health value
			//before it gets overwritten by the network
            maxHealth = health;
        }


        /// <summary>
        /// Initialize synced values on every client.
        /// </summary>
        public override void OnStartClient()
        {
			//get corresponding team and colorize renderers in team color
            Team team = GameManager.GetInstance().teams[teamIndex];
            for(int i = 0; i < renderers.Length; i++)
                renderers[i].material = team.material;
            
			//set name in label
            label.text = myName;
			//call hooks manually to update
            OnHealthChange(0, health);
            OnShieldChange(0, shield);
        }


        /// <summary>
        /// Initialize camera and input for this local client.
		/// This is being called after OnStartClient.
        /// </summary>
        public override void OnStartLocalPlayer()
        {
            //initialized already on host migration
            if (GameManager.GetInstance().localPlayer != null)
                return;

			//set a global reference to the local player
            GameManager.GetInstance().localPlayer = this;

			//get components and set camera target
            rb = GetComponent<Rigidbody>();
            camFollow = Camera.main.GetComponent<FollowTarget>();
            camFollow.target = turret;

			//initialize input controls for mobile devices
			//[0]=left joystick for movement, [1]=right joystick for shooting
            #if !UNITY_STANDALONE && !UNITY_WEBGL
            GameManager.GetInstance().ui.controls[0].onDrag += Move;
            GameManager.GetInstance().ui.controls[0].onDragEnd += MoveEnd;

            GameManager.GetInstance().ui.controls[1].onDragBegin += ShootBegin;
            GameManager.GetInstance().ui.controls[1].onDrag += RotateTurret;
            GameManager.GetInstance().ui.controls[1].onDrag += Shoot;
            #endif
        }


        //continously check for input on desktop platforms
        #if UNITY_EDITOR || UNITY_STANDALONE || UNITY_WEBGL
        void FixedUpdate()
		{
			//skip further calls for remote clients
            if (!isLocalPlayer)
			{
				//keep turret rotation updated for all clients
				OnTurretRotation(0, turretRotation);
				return;
			}

            //check for frozen Y position, regardless of other position constraints
            if((rb.constraints & RigidbodyConstraints.FreezePositionY) != RigidbodyConstraints.FreezePositionY)
            {
                //Y position is not locked and the player is above normal height, apply additional gravity
                if(transform.position.y > 0)
                    rb.AddForce(Physics.gravity * 2f, ForceMode.Acceleration);
            }
			
            #pragma warning disable 0219
			//movement variables
            Vector2 moveDir;
            Vector2 turnDir;
            #pragma warning restore 0219

			//reset moving input when no arrow keys are pressed down
            if( Input.GetAxisRaw("Horizontal") == 0 && Input.GetAxisRaw("Vertical") == 0)
            {
                moveDir.x = 0;
                moveDir.y = 0;
            }
            else
            {
				//read out moving directions and calculate force
                moveDir.x = Input.GetAxis("Horizontal");
                moveDir.y = Input.GetAxis("Vertical");
                Move(moveDir);
            }

            //cast a ray on a plane at the mouse position for detecting where to shoot 
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            Plane plane = new Plane(Vector3.up, Vector3.up);
            float distance = 0f;
            Vector3 hitPos = Vector3.zero;
            //the hit position determines the mouse position in the scene
            if (plane.Raycast(ray, out distance))
            {
                hitPos = ray.GetPoint(distance) - transform.position;
            }

            //we've converted the mouse position to a direction
            turnDir = new Vector2(hitPos.x, hitPos.z);

			//rotate turret to look at the mouse direction
            RotateTurret(new Vector2(hitPos.x, hitPos.z));
        }
        #endif
      
        
        //moves rigidbody in the direction passed in
        void Move(Vector2 direction = default(Vector2))
        {
            //if direction is not zero, rotate player in the moving direction relative to camera
            if (direction != Vector2.zero)
                transform.rotation = Quaternion.LookRotation(new Vector3(direction.x, 0, direction.y))
                                     * Quaternion.Euler(0, camFollow.camTransform.eulerAngles.y, 0);
            
            //create movement vector based on current rotation and speed
            Vector3 movementDir = transform.forward * moveSpeed * Time.deltaTime;
            //apply vector to rigidbody position
            rb.MovePosition(rb.position + movementDir);
        }


        //on movement drag ended
        void MoveEnd()
        {
			//reset rigidbody physics values
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        
        
        //rotates turret to the direction passed in
        void RotateTurret(Vector2 direction = default(Vector2))
        {
			//don't rotate without values
            if(direction == Vector2.zero)
                return;

            //get rotation value as angle out of the direction we received
            int newRotation = (int)(Quaternion.LookRotation(new Vector3(direction.x, 0, direction.y)).eulerAngles.y + camFollow.camTransform.eulerAngles.y);
            
            //limit rotation value send rate to server:
			//only send every 'sendRate' seconds and skip minor incremental changes
            if(Time.time >= nextRotate && (Mathf.Abs(Mathf.DeltaAngle(turretRotation, newRotation)) > 5))
            {
				//set next update timestamp and send to server
                nextRotate = Time.time + sendRate;
                turretRotation = newRotation;
                CmdRotateTurret(newRotation);
            }

            turret.rotation = Quaternion.Euler(0, newRotation, 0);
        }
        
        
        //Command telling the server the updated turret rotation
		[Command]
        void CmdRotateTurret(int value)
        {
            turretRotation = value;
        }

        
        
        //hook for updating turret rotation locally
        void OnTurretRotation(int oldValue, int newValue)
        {
            //ignore value updates for our own player,
            //so we can update the rotation server-independent
            if (isLocalPlayer) return;

            turretRotation = newValue;
            turret.rotation = Quaternion.Euler(0, turretRotation, 0);
        }
        
        
        //hook for updating health locally
        protected void OnHealthChange(int oldValue, int newValue)
        {
            health = newValue;
            healthSlider.value = (float)health / maxHealth;
        }


        //hook for updating shield locally
        protected void OnShieldChange(int oldValue, int newValue)
        {
            shield = newValue;
            shieldSlider.value = shield;
        }

        
        //called on all clients on both player death and respawn
		//only difference is that on respawn, the client sends the request
		[ClientRpc]
        protected virtual void RpcRespawn(short senderId)
        {
			//toggle visibility for player gameobject (on/off)
            gameObject.SetActive(!gameObject.activeInHierarchy);
            bool isActive = gameObject.activeInHierarchy;
            killedBy = null;

			//the player has been killed
			if(!isActive)
            {
                //find original sender game object (killedBy)
                GameObject senderObj = null;
                if(senderId > 0 && NetworkIdentity.spawned.ContainsKey((uint)senderId))
                {
                    senderObj = NetworkIdentity.spawned[(uint)senderId].gameObject;
                    if (senderObj != null) killedBy = senderObj;
                }
                
                //detect whether the current user was responsible for the kill, but not for suicide
                //yes, that's my kill: increase local kill counter
                if (this != GameManager.GetInstance().localPlayer && killedBy == GameManager.GetInstance().localPlayer.gameObject)
                {
                    GameManager.GetInstance().ui.killCounter[0].text = (int.Parse(GameManager.GetInstance().ui.killCounter[0].text) + 1).ToString();
                    GameManager.GetInstance().ui.killCounter[0].GetComponent<Animator>().Play("Animation");
                }
            }

            if (isServer)
            {
                //send player back to the team area, this will get overwritten by the exact position from the client itself later on
                //we just do this to avoid players "popping up" from the position they died and then teleporting to the team area instantly
                //this is manipulating the internal PhotonTransformView cache to update the networkPosition variable
                transform.position = GameManager.GetInstance().GetSpawnPosition(teamIndex);
            }

            //further changes only affect the local client
            if (!isLocalPlayer)
                return;

			//local player got respawned so reset states
            if(isActive == true)
                ResetPosition();
            else
            {
				//local player was killed, set camera to follow the killer
                if(killedBy != null) camFollow.target = killedBy.transform;
				//hide input controls and other HUD elements
                camFollow.HideMask(true);
				//display respawn window (only for local player)
                GameManager.GetInstance().DisplayDeath();
            } 
        }
        
        
        /// <summary>
        /// Command telling the server that this client is ready for respawn.
		/// This is when the respawn delay is over or a video ad has been watched.
        /// </summary>
		[Command]
        public void CmdRespawn()
        {
            RpcRespawn((short)0);
        }
        
        
        /// <summary>
        /// Repositions in team area and resets camera & input variables.
		/// This should only be called for the local player.
        /// </summary>
        public void ResetPosition()
        {
			//start following the local player again
            camFollow.target = turret;
            camFollow.HideMask(false);

			//get team area and reposition it there
            transform.position = GameManager.GetInstance().GetSpawnPosition(teamIndex);
            
			//reset forces modified by input
			rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            transform.rotation = Quaternion.identity;
        }


        /// <summary>
        /// Called on all clients on game end providing the winning team.
        /// This is when a target kill count or goal (e.g. flag captured) was achieved.
        /// </summary>
        [ClientRpc]
        public void RpcGameOver(int teamIndex)
        {
			//display game over window
            GameManager.GetInstance().DisplayGameOver(teamIndex);
        }
    }
}

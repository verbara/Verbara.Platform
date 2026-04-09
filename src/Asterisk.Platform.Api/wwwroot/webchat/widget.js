(function() {
  'use strict';

  var script = document.currentScript;
  var tenantId = script.getAttribute('data-tenant');
  var position = script.getAttribute('data-position') || 'bottom-right';
  var title = script.getAttribute('data-title') || 'Chat with us';
  var baseUrl = script.src.replace(/\/webchat\/widget\.js.*$/, '');
  var apiBase = baseUrl + '/api/v1';

  if (!tenantId) {
    console.error('[WebChat] data-tenant attribute is required');
    return;
  }

  // Load CSS
  var link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = baseUrl + '/webchat/widget.css';
  document.head.appendChild(link);

  // State
  var sessionId = null;
  var ws = null;
  var isOpen = false;
  var branding = null;

  // DOM elements
  var bubble, panel, messagesEl, inputEl, sendBtn, typingEl;

  // Fetch branding
  fetch(apiBase + '/branding/' + tenantId)
    .then(function(r) { return r.ok ? r.json() : null; })
    .then(function(b) {
      branding = b;
      init();
    })
    .catch(function() { init(); });

  function init() {
    createBubble();
    createPanel();
    applyBranding();

    // Restore session from localStorage
    var saved = localStorage.getItem('ast_webchat_' + tenantId);
    if (saved) {
      try {
        var data = JSON.parse(saved);
        sessionId = data.sessionId;
      } catch(e) { /* ignore */ }
    }
  }

  function createBubble() {
    bubble = document.createElement('button');
    bubble.className = 'ast-webchat-bubble';
    bubble.setAttribute('aria-label', 'Open chat');
    bubble.innerHTML = '<svg viewBox="0 0 24 24"><path d="M20 2H4c-1.1 0-2 .9-2 2v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H5.2L4 17.2V4h16v12z"/></svg>';
    bubble.onclick = togglePanel;
    if (position === 'bottom-left') {
      bubble.style.right = 'auto';
      bubble.style.left = '24px';
    }
    document.body.appendChild(bubble);
  }

  function createPanel() {
    panel = document.createElement('div');
    panel.className = 'ast-webchat-panel';
    panel.hidden = true;

    var headerTitle = branding && branding.displayName ? branding.displayName : title;
    var logoHtml = branding && branding.logoUrl
      ? '<img src="' + escapeHtml(branding.logoUrl) + '" alt="">'
      : '';

    panel.innerHTML =
      '<div class="ast-webchat-header">' +
        logoHtml +
        '<span class="ast-webchat-header-title">' + escapeHtml(headerTitle) + '</span>' +
        '<button class="ast-webchat-close" aria-label="Close chat">&times;</button>' +
      '</div>' +
      '<div class="ast-webchat-messages"></div>' +
      '<div class="ast-webchat-typing" hidden>Agent is typing...</div>' +
      '<div class="ast-webchat-input-area">' +
        '<textarea class="ast-webchat-input" rows="1" placeholder="Type a message..."></textarea>' +
        '<button class="ast-webchat-send" disabled>Send</button>' +
      '</div>';

    if (position === 'bottom-left') {
      panel.style.right = 'auto';
      panel.style.left = '24px';
    }

    document.body.appendChild(panel);

    panel.querySelector('.ast-webchat-close').onclick = togglePanel;
    messagesEl = panel.querySelector('.ast-webchat-messages');
    inputEl = panel.querySelector('.ast-webchat-input');
    sendBtn = panel.querySelector('.ast-webchat-send');
    typingEl = panel.querySelector('.ast-webchat-typing');

    inputEl.oninput = function() {
      sendBtn.disabled = !inputEl.value.trim();
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'typing', text: null }));
      }
    };
    inputEl.onkeydown = function(e) {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
      }
    };
    sendBtn.onclick = sendMessage;
  }

  function applyBranding() {
    if (!branding || !branding.primaryColor) return;
    document.documentElement.style.setProperty('--ast-primary', branding.primaryColor);
  }

  function togglePanel() {
    isOpen = !isOpen;
    panel.hidden = !isOpen;
    bubble.style.display = isOpen ? 'none' : 'flex';
    if (isOpen && !sessionId) {
      createSession();
    } else if (isOpen && sessionId && (!ws || ws.readyState !== WebSocket.OPEN)) {
      connectWebSocket();
    }
    if (isOpen) inputEl.focus();
  }

  function createSession() {
    fetch(apiBase + '/webchat/sessions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ tenantId: tenantId })
    })
    .then(function(r) { return r.json(); })
    .then(function(data) {
      sessionId = data.sessionId;
      localStorage.setItem('ast_webchat_' + tenantId,
        JSON.stringify({ sessionId: sessionId }));
      connectWebSocket();
    })
    .catch(function(err) {
      appendSystemMessage('Unable to connect. Please try again.');
      console.error('[WebChat] Session creation failed:', err);
    });
  }

  function connectWebSocket() {
    var protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    var host = new URL(baseUrl).host;
    var wsUrl = protocol + '//' + host + '/ws/webchat/' + sessionId;

    ws = new WebSocket(wsUrl);

    ws.onopen = function() {
      sendBtn.disabled = !inputEl.value.trim();
    };

    ws.onmessage = function(event) {
      try {
        var msg = JSON.parse(event.data);
        if (msg.type === 'message' && msg.data) {
          appendInboundMessage(msg.data);
        } else if (msg.type === 'typing') {
          showTyping();
        } else if (msg.type === 'ended') {
          appendSystemMessage('Conversation ended');
          ws.close();
        }
      } catch(e) { /* ignore parse errors */ }
    };

    ws.onclose = function() {
      sendBtn.disabled = true;
    };
  }

  function sendMessage() {
    var text = inputEl.value.trim();
    if (!text) return;

    appendOutboundMessage(text);
    inputEl.value = '';
    sendBtn.disabled = true;

    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'message', text: text }));
    } else {
      // REST fallback
      fetch(apiBase + '/webchat/sessions/' + sessionId + '/messages', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: text })
      }).catch(function(err) {
        console.error('[WebChat] REST fallback failed:', err);
      });
    }
  }

  function appendOutboundMessage(text) {
    var div = document.createElement('div');
    div.className = 'ast-webchat-msg ast-webchat-msg--out';
    div.textContent = text;
    messagesEl.appendChild(div);
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function appendInboundMessage(data) {
    hideTyping();
    var div = document.createElement('div');
    div.className = 'ast-webchat-msg ast-webchat-msg--in';
    if (data.blocks) {
      var texts = [];
      for (var i = 0; i < data.blocks.length; i++) {
        var b = data.blocks[i];
        if (b.text || b.content) texts.push(b.text || b.content);
      }
      div.textContent = texts.join('\n') || '[Media]';
    } else {
      div.textContent = '[Message]';
    }
    messagesEl.appendChild(div);
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function appendSystemMessage(text) {
    var div = document.createElement('div');
    div.className = 'ast-webchat-msg ast-webchat-msg--in';
    div.style.fontStyle = 'italic';
    div.textContent = text;
    messagesEl.appendChild(div);
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  var typingTimer;
  function showTyping() {
    typingEl.hidden = false;
    messagesEl.scrollTop = messagesEl.scrollHeight;
    clearTimeout(typingTimer);
    typingTimer = setTimeout(hideTyping, 3000);
  }

  function hideTyping() {
    typingEl.hidden = true;
    clearTimeout(typingTimer);
  }

  function escapeHtml(str) {
    var div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
  }
})();

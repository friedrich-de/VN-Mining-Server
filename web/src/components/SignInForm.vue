<script setup lang="ts">
import { ref } from 'vue';

defineProps<{
  busy: boolean;
  error: string | null;
}>();

const emit = defineEmits<{ submit: [key: string] }>();

const key = ref('');

function submit(): void {
  if (key.value.trim() === '') return;
  emit('submit', key.value.trim());
}
</script>

<template>
  <section class="sign-in" aria-labelledby="sign-in-title">
    <h1 id="sign-in-title">Sign in</h1>
    <p class="muted">
      Enter this server's API key. The server keeps it in the file api-key.txt of its data folder,
      unless it was given one when it was set up. This browser stays signed in.
    </p>

    <form novalidate @submit.prevent="submit">
      <!-- A name for password managers to keep the key under. -->
      <input
        class="visually-hidden"
        type="text"
        name="username"
        value="VN Miner"
        autocomplete="username"
        tabindex="-1"
        aria-hidden="true"
        readonly
      />

      <label for="sign-in-key">API key</label>
      <input
        id="sign-in-key"
        v-model="key"
        type="password"
        name="password"
        autocomplete="current-password"
        autocapitalize="none"
        spellcheck="false"
        required
        :disabled="busy"
      />

      <p v-if="error" class="error" role="alert">{{ error }}</p>

      <div class="actions">
        <button class="button primary" type="submit" :disabled="busy">
          {{ busy ? 'Signing in…' : 'Sign in' }}
        </button>
      </div>
    </form>
  </section>
</template>

<style scoped>
.sign-in {
  display: flex;
  flex-direction: column;
  gap: 16px;
  width: 100%;
  max-width: 400px;
  margin: 0 auto;
  padding: 32px 16px;
}

h1 {
  font-size: 24px;
  font-weight: 600;
  color: var(--text-strong);
}

form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

label {
  margin-top: 8px;
  font-size: 13px;
  color: var(--text-muted);
}

input {
  min-height: 44px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  color: var(--text-strong);
}

input:focus-visible {
  outline-offset: 0;
  border-color: var(--accent);
}

.error {
  color: var(--danger);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 16px;
}
</style>

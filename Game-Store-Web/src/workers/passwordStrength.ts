/// <reference lib="webworker" />
import { ZxcvbnFactory } from '@zxcvbn-ts/core'
import { adjacencyGraphs, dictionary as commonDictionary } from '@zxcvbn-ts/language-common'
import { dictionary as englishDictionary, translations } from '@zxcvbn-ts/language-en'

const estimator = new ZxcvbnFactory({
  graphs: adjacencyGraphs,
  dictionary: { ...commonDictionary, ...englishDictionary },
  translations,
})

self.onmessage = (event: MessageEvent<{ id: number; password: string; email: string }>) => {
  const { id, password, email } = event.data
  try {
    if (!password || password.length > 128) throw new Error('Invalid input length')
    const inputs = ['gamestore', 'game store', ...email.toLowerCase().split(/[@.\s_-]+/).filter(part => part.length >= 3)]
    const result = estimator.check(password, inputs)
    self.postMessage({ id, score: result.score, warning: result.feedback.warning, suggestions: result.feedback.suggestions })
  } catch {
    self.postMessage({ id, error: 'Password strength could not be checked. Try editing the password again.' })
  }
}
